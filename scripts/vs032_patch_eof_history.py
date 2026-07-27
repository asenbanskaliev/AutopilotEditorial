from pathlib import Path

path = Path("tests/BookStudio.Tests.OpenCodeSseReconciliation/OpenCodeSseReconciliationJourney.cs")
text = path.read_text(encoding="utf-8")
old = '''            item => streamCalls >= 2 &&
                    item.Source == OpenCodeEventSources.Poll &&
                    item.SessionId == "ses_repair" &&
                    item.Status?.Type == OpenCodeSessionStatusTypes.Idle,
            timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);'''
new = '''            items => streamCalls >= 2 &&
                     items.Any(item =>
                         item.Source == OpenCodeEventSources.Poll &&
                         item.SessionId == "ses_repair" &&
                         item.Status?.Type == OpenCodeSessionStatusTypes.Idle),
            timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);'''
if old not in text:
    if new in text:
        raise SystemExit(0)
    raise SystemExit("EOF history predicate anchor missing")
path.write_text(text.replace(old, new, 1), encoding="utf-8")
