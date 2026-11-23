using GameTuner.Core.Tuning;

namespace GameTuner.Core.Tests;

public class PowercfgTests
{
    private const string English = """
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
          Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)
            GUID Alias: SUB_PROCESSOR
            Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Processor performance boost mode)
              GUID Alias: PERFBOOSTMODE
              Possible Setting Index: 000
              Possible Setting Friendly Name: Disabled
              Possible Setting Index: 002
              Possible Setting Friendly Name: Aggressive
            Current AC Power Setting Index: 0x00000002
            Current DC Power Setting Index: 0x00000001
        """;

    private const string Russian = """
        GUID схемы питания: 381b4222-f694-41f0-9685-ff5bb260df2e  (Сбалансированная)
          GUID подгруппы: 54533251-82be-4824-96c1-47b60b740d00  (Управление питанием процессора)
            Параметр питания GUID: be337238-0d82-4146-a960-4f3749d470c7  (Режим повышения производительности процессора)
              Возможный индекс параметра: 000
            Текущий индекс параметра питания от сети переменного тока: 0x00000003
            Текущий индекс параметра питания от сети постоянного тока: 0x00000000
        """;

    [Fact]
    public void ParsesEnglishOutput() =>
        Assert.Equal<(int, int)?>((2, 1), Powercfg.ParseCurrentIndexes(English));

    [Fact]
    public void ParsesLocalizedOutput() =>
        Assert.Equal<(int, int)?>((3, 0), Powercfg.ParseCurrentIndexes(Russian));

    [Fact]
    public void ReturnsNullWhenSettingIsMissing() =>
        Assert.Null(Powercfg.ParseCurrentIndexes("Invalid Parameters -- try \"/?\" for help"));
}
