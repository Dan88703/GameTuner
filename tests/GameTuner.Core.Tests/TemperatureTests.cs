using GameTuner.Core.Monitoring;

namespace GameTuner.Core.Tests;

public class TemperatureTests
{
    [Theory]
    [InlineData(45.5, true)]
    [InlineData(95, true)]
    [InlineData(0, false)]        // no sensor
    [InlineData(-273.15, false)]  // raw 0 K
    [InlineData(150, false)]      // garbage
    public void ThermalZone_PlausibleRange(double celsius, bool expected) =>
        Assert.Equal(expected, ThermalZoneCounter.IsPlausible(celsius));

    [Fact]
    public void Merge_KeepsAcpiTemperature_WhenLhmHasNoCpuSensor()
    {
        // Typical "no PawnIO" case: LHM runs, but its CPU temperature is missing.
        var basic = new SensorSnapshot { CpuTempC = 68, CpuTempIsAcpi = true };
        var detailed = new SensorSnapshot { CpuLoad = 40, CpuTempC = null, GpuTempC = 61 };

        var merged = SensorService.Merge(basic, detailed);

        Assert.Equal(68, merged.CpuTempC);
        Assert.True(merged.CpuTempIsAcpi);
        Assert.Equal(61, merged.GpuTempC);
    }

    [Fact]
    public void Merge_PrefersPreciseCpuSensorOverAcpi()
    {
        var basic = new SensorSnapshot { CpuTempC = 60, CpuTempIsAcpi = true };
        var detailed = new SensorSnapshot { CpuTempC = 72 };

        var merged = SensorService.Merge(basic, detailed);

        Assert.Equal(72, merged.CpuTempC);
        Assert.False(merged.CpuTempIsAcpi);
    }
}
