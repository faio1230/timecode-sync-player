using FluentAssertions;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests.LatchLifetime;

/// <summary>
/// v0.5.2 段 0: ラッチの寿命の特性テスト（characterization test）。寿命の表
/// （<see cref="LatchLifetimeTable"/>）の各行について、今のコードで
/// 「ラッチを立てる → できごとを起こす → 現状の列どおりに立っている／下りている」を確かめる。
/// 段 1・2（振る舞いを変えない作り直し）の間、このテストが緑のままであることが判定になる。
/// </summary>
public sealed class LatchLifetimeTests
{
    public static IEnumerable<object[]> TestedRows() =>
        LatchLifetimeTable.Rows
            .Where(row => row.Current != CurrentBehavior.NotApplicable)
            .Select(row => new object[] { row.Latch.ToString(), row.Event });

    public static IEnumerable<object[]> NotApplicableRows() =>
        LatchLifetimeTable.Rows
            .Where(row => row.Current == CurrentBehavior.NotApplicable)
            .Select(row => new object[] { row.Latch.ToString(), row.Event });

    [Theory]
    [MemberData(nameof(TestedRows))]
    public void Latch_AfterEvent_MatchesCurrentBehavior(string latch, LifecycleEvent evt)
    {
        LatchLifetimeRow row = Find(latch, evt);
        LatchLifetimeScenario s = LatchArrangements.Arrange(row.Latch, LatchArrangements.ModeFor(row.Latch, evt));
        s.Read(row.Latch).Should().BeTrue("配置でラッチが立っていること（{0}）", row);
        s.Prepare(evt).Should().BeTrue("できごとの前提ができること（{0}）", row);
        s.Read(row.Latch).Should().BeTrue("前提を作った後もラッチが立っていること（{0}）", row);

        s.Fire(evt);

        s.Read(row.Latch).Should().Be(row.Current == CurrentBehavior.Keeps,
            "表の現状は {0}（根拠: {1}）", row.Current, row.Evidence);
    }

    /// <summary>
    /// NotApplicable の行は「ラッチを立てたままそのできごとを起こせない」ことを確かめる
    /// （前提を作るとラッチが消える、前提が作れない、またはギャップが無い Single の配置）。
    /// </summary>
    [Theory]
    [MemberData(nameof(NotApplicableRows))]
    public void NotApplicableRow_CannotHoldLatchIntoEvent(string latch, LifecycleEvent evt)
    {
        LatchLifetimeRow row = Find(latch, evt);
        SyncMode mode = LatchArrangements.ModeFor(row.Latch, evt);
        if (evt is LifecycleEvent.GapEnter or LifecycleEvent.GapExit && mode == SyncMode.Single)
        {
            row.Evidence.Should().Contain("ギャップは Continue にしか無く");
            return;
        }

        LatchLifetimeScenario s = LatchArrangements.Arrange(row.Latch, mode);
        s.Read(row.Latch).Should().BeTrue("配置でラッチが立っていること（{0}）", row);
        bool prepared = s.Prepare(evt);
        (prepared && s.Read(row.Latch)).Should().BeFalse(
            "NotApplicable の行は前提でラッチが消えるか前提が作れない（根拠: {0}）", row.Evidence);
    }

    [Fact]
    public void Table_CoversEveryLatchAndEventExactlyOnce()
    {
        var expected = LatchArrangements.All
            .SelectMany(latch => Enum.GetValues<LifecycleEvent>().Select(evt => (latch, evt)))
            .ToList();
        var actual = LatchLifetimeTable.Rows.Select(row => (row.Latch, row.Event)).ToList();

        actual.Should().OnlyHaveUniqueItems();
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Table_EveryRowHasEvidenceWithLineNumbers()
    {
        LatchLifetimeTable.Rows.Where(row => !row.Evidence.Contains(".cs:") &&
                                             !row.Evidence.Contains("ギャップは Continue にしか無く"))
            .Should().BeEmpty("根拠は ファイル名:行 で書く");
    }

    [Fact]
    public void Table_DiscrepancyRowsAreDesignSection6Candidates()
    {
        LatchLifetimeTable.Rows.Where(row => row.IsDiscrepancy)
            .Should().OnlyContain(row => row.Evidence.StartsWith("§6 の "),
                "現状と意図が食い違う行は設計書 §6 の候補そのもの");
        LatchLifetimeTable.Rows.Where(row => row.Evidence.StartsWith("§6 の "))
            .Should().OnlyContain(row => row.IsDiscrepancy);
    }

    private static LatchLifetimeRow Find(string latch, LifecycleEvent evt) =>
        LatchLifetimeTable.Rows.Single(row => row.Latch.ToString() == latch && row.Event == evt);
}
