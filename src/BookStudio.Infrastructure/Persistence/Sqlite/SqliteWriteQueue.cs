using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite;

/// <summary>Serializes all write and exclusive database operations for one workspace.</summary>
public sealed class SqliteWriteQueue : IAsyncDisposable
{
    private readonly Channel<WorkItem> _channel;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly Task _processor;
    private int _disposed;

    public SqliteWriteQueue(
        SqliteConnectionFactory connectionFactory,
        int capacity)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _processor = ProcessAsync();
    }

    public ValueTask<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return EnqueueAsync(new TransactionWorkItem<T>(operation, cancellationToken), cancellationToken);
    }

    public ValueTask<T> ExecuteExclusiveAsync<T>(
        Func<SqliteConnection, CancellationToken, T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return EnqueueAsync(new ExclusiveWorkItem<T>(operation, cancellationToken), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        await _processor.ConfigureAwait(false);
    }

    private async ValueTask<T> EnqueueAsync<T>(
        WorkItem<T> item,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        return await item.Task.ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            item.Execute(_connectionFactory);
        }
    }

    private abstract class WorkItem
    {
        protected WorkItem(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
        }

        protected CancellationToken CancellationToken { get; }

        public abstract void Execute(SqliteConnectionFactory connectionFactory);
    }

    private abstract class WorkItem<T> : WorkItem
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected WorkItem(CancellationToken cancellationToken)
            : base(cancellationToken)
        {
        }

        public Task<T> Task => _completion.Task;

        protected void Complete(T result) => _completion.TrySetResult(result);

        protected void Fail(Exception exception) => _completion.TrySetException(exception);

        protected void Cancel() => _completion.TrySetCanceled(CancellationToken);
    }

    private sealed class TransactionWorkItem<T> : WorkItem<T>
    {
        private readonly Func<SqliteConnection, SqliteTransaction, CancellationToken, T> _operation;

        public TransactionWorkItem(
            Func<SqliteConnection, SqliteTransaction, CancellationToken, T> operation,
            CancellationToken cancellationToken)
            : base(cancellationToken)
        {
            _operation = operation;
        }

        public override void Execute(SqliteConnectionFactory connectionFactory)
        {
            if (CancellationToken.IsCancellationRequested)
            {
                Cancel();
                return;
            }

            try
            {
                using var connection = connectionFactory.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var result = _operation(connection, transaction, CancellationToken);
                CancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                Complete(result);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                Cancel();
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }
    }

    private sealed class ExclusiveWorkItem<T> : WorkItem<T>
    {
        private readonly Func<SqliteConnection, CancellationToken, T> _operation;

        public ExclusiveWorkItem(
            Func<SqliteConnection, CancellationToken, T> operation,
            CancellationToken cancellationToken)
            : base(cancellationToken)
        {
            _operation = operation;
        }

        public override void Execute(SqliteConnectionFactory connectionFactory)
        {
            if (CancellationToken.IsCancellationRequested)
            {
                Cancel();
                return;
            }

            try
            {
                using var connection = connectionFactory.OpenConnection();
                var result = _operation(connection, CancellationToken);
                CancellationToken.ThrowIfCancellationRequested();
                Complete(result);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                Cancel();
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }
    }
}
