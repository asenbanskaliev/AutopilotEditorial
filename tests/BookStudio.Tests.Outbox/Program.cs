using BookStudio.Tests.Integration;
using BookStudio.Tests.Outbox;
using Microsoft.Data.Sqlite;

var workspaceRoot = Path.Combine(
    Path.GetTempPath(),
    "BookStudio.Tests.Outbox",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspaceRoot);

try
{
    await OutboxJourney.RunAsync(Path.Combine(workspaceRoot, "legacy"));
    await TransactionalOutboxJourney.RunAsync(Path.Combine(workspaceRoot, "transactional"));
    await SchedulerJourney.RunAsync(Path.Combine(workspaceRoot, "scheduler"));
    await WorkerExecutionJourney.RunAsync(Path.Combine(workspaceRoot, "worker"));
    WorkflowCatalogJourney.Run(Directory.GetCurrentDirectory());
    await HumanGateJourney.RunAsync(Path.Combine(workspaceRoot, "human-gates"));
    await ExecutionControlJourney.RunAsync(Path.Combine(workspaceRoot, "execution-control"));
    await DeadLetterRecoveryJourney.RunAsync(Path.Combine(workspaceRoot, "dead-letter-recovery"));
    await ConcurrencyLimitJourney.RunAsync(Path.Combine(workspaceRoot, "concurrency-limits"));
    await ProjectJourney.RunAsync(Path.Combine(workspaceRoot, "project-journey"));
    await DiscoveryJourney.RunAsync(Path.Combine(workspaceRoot, "discovery-journey"));
    await EditorialProposalJourney.RunAsync(Path.Combine(workspaceRoot, "editorial-proposal"));
    await SpecificationJourney.RunAsync(Path.Combine(workspaceRoot, "specification-lifecycle"));
    await BookPlanJourney.RunAsync(Path.Combine(workspaceRoot, "book-planning"));
    await ScenePlanJourney.RunAsync(Path.Combine(workspaceRoot, "scene-planning"));
    await SceneGenerationJourney.RunAsync(Path.Combine(workspaceRoot, "scene-generation"));
    await ParagraphCoherenceJourney.RunAsync(Path.Combine(workspaceRoot, "paragraph-coherence"));
    await SceneCoherenceJourney.RunAsync(Path.Combine(workspaceRoot, "scene-coherence"));
    await TransitionAuditJourney.RunAsync(Path.Combine(workspaceRoot, "transition-audit"));
    await KnowledgeStateJourney.RunAsync(Path.Combine(workspaceRoot, "knowledge-state"));
    await CharacterObjectStateJourney.RunAsync(Path.Combine(workspaceRoot, "character-object-state"));
    await TimelinePlotJourney.RunAsync(Path.Combine(workspaceRoot, "timeline-plot"));
    await RepairPatchJourney.RunAsync(Path.Combine(workspaceRoot, "repair-patches"));
    await ChapterGateJourney.RunAsync(Path.Combine(workspaceRoot, "chapter-gate"));
    await MemoryCommitJourney.RunAsync(Path.Combine(workspaceRoot, "memory-commit"));
    await CrossChapterAuditJourney.RunAsync(Path.Combine(workspaceRoot, "cross-chapter-audit"));
    await EditorialPassOrchestrationJourney.RunAsync(Path.Combine(workspaceRoot, "editorial-pass-orchestration"));
    await DevelopmentalEditingJourney.RunAsync(Path.Combine(workspaceRoot, "developmental-editing"));
    await StructuralContentEditingJourney.RunAsync(Path.Combine(workspaceRoot, "structural-content-editing"));
    await VoiceLineEditingJourney.RunAsync(Path.Combine(workspaceRoot, "voice-line-editing"));
    await DialogueEditingJourney.RunAsync(Path.Combine(workspaceRoot, "dialogue-editing"));
    await ThemesPacingEditingJourney.RunAsync(Path.Combine(workspaceRoot, "themes-pacing-editing"));
    await CopyeditProofreadingJourney.RunAsync(Path.Combine(workspaceRoot, "copyedit-proofreading"));
    await BetaReaderReviewJourney.RunAsync(Path.Combine(workspaceRoot, "beta-reader-review"));
    await OriginalityReadAloudReviewJourney.RunAsync(Path.Combine(workspaceRoot, "originality-read-aloud-review"));
    await ResearchPlanningJourney.RunAsync(Path.Combine(workspaceRoot, "research-planning"));
    Console.WriteLine("RESEARCH_PLANNING_PASS schema=PASS authority=PASS questions=PASS decisions=PASS blocking_gate=PASS replay=PASS history=PASS outbox_once=PASS restart=PASS isolation=PASS mutation=NONE");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("RESEARCH_PLANNING_FAIL: " + exception);
    return 1;
}
finally
{
    SqliteConnection.ClearAllPools();
    TryDelete(workspaceRoot);
}

static void TryDelete(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
        // Integration cleanup is best effort.
    }
    catch (UnauthorizedAccessException)
    {
        // Integration cleanup is best effort.
    }
}
