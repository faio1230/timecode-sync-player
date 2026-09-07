using System.IO;
using System.Text.Json;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class MonkeyTestSupportTests
{
    [Fact]
    public void Parse_WhenEnabled_RequiresAbsoluteReportDirectory()
    {
        var environment = ValidEnvironment();
        environment[MonkeyTestConfiguration.ReportDirectoryVariable] = "relative-report";

        Action act = () => MonkeyTestConfiguration.Parse(environment);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TIMECODE_MONKEY_REPORT_DIR*absolute*");
    }

    [Fact]
    public void Parse_WhenEnabled_RejectsActionCountOutsideContract()
    {
        var environment = ValidEnvironment();
        environment[MonkeyTestConfiguration.ActionCountVariable] = "100001";

        Action act = () => MonkeyTestConfiguration.Parse(environment);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TIMECODE_MONKEY_ACTIONS*1..100000*");
    }

    [Fact]
    public void Generate_SameSeedReplaysSequence_AndDifferentSeedChangesIt()
    {
        IReadOnlyList<MonkeyOperation> first = MonkeyOperationGenerator.Generate(123, 50);
        IReadOnlyList<MonkeyOperation> replay = MonkeyOperationGenerator.Generate(123, 50);
        IReadOnlyList<MonkeyOperation> differentSeed = MonkeyOperationGenerator.Generate(124, 50);

        replay.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
        differentSeed.Should().NotBeEquivalentTo(first);
    }

    [Fact]
    public void Generate_KnownCorpus_HasRandomOrderingAndBoundedReplayParameters()
    {
        IReadOnlyList<MonkeyOperation> operations = MonkeyOperationGenerator.Generate(456, 250);
        MonkeyOperationKind[] kinds = operations.Select(operation => operation.Kind).ToArray();

        kinds.Should().Contain(Enum.GetValues<MonkeyOperationKind>());
        kinds.Take(13).Should().NotEqual(kinds.Skip(13).Take(13));
        kinds.Zip(kinds.Skip(1), (left, right) => left == right)
            .Should().Contain(true, "independent PRNG draws may repeat adjacent operations");
        operations.Should().OnlyContain(operation =>
            operation.Index >= 0 && operation.Index < 250 &&
            operation.SeekRatios.Count == 3 &&
            operation.SeekRatios.All(ratio => ratio >= 0 && ratio <= 1) &&
            operation.SyncModeIndex >= 0 && operation.SyncModeIndex <= 1 &&
            operation.GapBehaviorIndex >= 0 && operation.GapBehaviorIndex <= 1 &&
            operation.SignalLossModeIndex >= 0 && operation.SignalLossModeIndex <= 1 &&
            operation.SignalStartSeconds >= 0 && operation.SignalStartSeconds <= 15 &&
            operation.NoiseAmplitude >= 0.020 && operation.NoiseAmplitude <= 0.150);
    }

    [Fact]
    public void SerializeSummary_UsesRunnerContractFields()
    {
        var summary = new MonkeyRunSummary
        {
            Success = true,
            Seed = 77,
            RequestedActions = 10,
            CompletedActions = 10,
            ExecutedActions = 8,
            AttemptedActions = 10,
            UnavailableActions = 2,
        };

        using JsonDocument document = JsonDocument.Parse(MonkeyJson.SerializeSummary(summary));
        JsonElement root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("seed").GetInt32().Should().Be(77);
        root.GetProperty("requestedActions").GetInt32().Should().Be(10);
        root.GetProperty("completedActions").GetInt32().Should().Be(10);
        root.GetProperty("executedActions").GetInt32().Should().Be(8);
        root.GetProperty("attemptedActions").GetInt32().Should().Be(10);
        root.GetProperty("unavailableActions").GetInt32().Should().Be(2);
    }

    [Fact]
    public void Journal_Write_IsVisibleBeforeJournalIsDisposed()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tsp-monkey-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "monkey.jsonl");

        try
        {
            using var journal = new MonkeyJournal(path, seed: 55);
            journal.Write("blocking-start", details: new { action = "wait-for-window" });

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string persisted = reader.ReadToEnd();
            persisted.Should().Contain("\"event\":\"blocking-start\"");
            persisted.Should().Contain("\"seed\":55");
            persisted.Should().Contain("\"action\":\"wait-for-window\"");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Dictionary<string, string?> ValidEnvironment() => new()
    {
        [MonkeyTestConfiguration.EnabledVariable] = "1",
        [MonkeyTestConfiguration.SeedVariable] = "123",
        [MonkeyTestConfiguration.ActionCountVariable] = "250",
        [MonkeyTestConfiguration.ReportDirectoryVariable] = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "tsp-monkey-report")),
    };
}
