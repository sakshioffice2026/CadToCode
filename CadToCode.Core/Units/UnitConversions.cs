using System;

namespace CadToCode.Core.Units;

/// <summary>
/// Unit conversion helpers. The platform stores everything in SI internally:
/// length m, pressure Pa, flow m3/s, angular speed rad/s, velocity m/s, power W, density kg/m3, temperature K.
/// Use these helpers only at system boundaries (user input, display, CAD export in mm).
/// </summary>
public static class UnitConversions
{
    // ---- Exact / standard factors ----
    public const double MetersPerInch = 0.0254;
    public const double MetersPerFoot = 0.3048;
    public const double MetersPerMillimeter = 0.001;

    /// <summary>1 CFM (ft3/min) in m3/s.</summary>
    public const double CubicMetersPerSecondPerCfm = 0.000471947443;

    /// <summary>1 inch of water column (at 4 degC) in Pa.</summary>
    public const double PascalsPerInchWaterGauge = 249.08891;

    public const double PascalsPerPsi = 6894.757293168;
    public const double PascalsPerKilopascal = 1000.0;
    public const double PascalsPerBar = 100000.0;
    public const double PascalsPerAtmosphere = 101325.0;
    public const double PascalsPerMmWaterGauge = 9.80665;

    public const double WattsPerHorsepower = 745.69987158227;
    public const double KilogramsPerPound = 0.45359237;
    public const double KgPerM3PerLbPerFt3 = 16.01846337396;

    /// <summary>Standard air density at 20 degC, 101.325 kPa (kg/m3).</summary>
    public const double StandardAirDensityKgM3 = 1.204;

    /// <summary>AMCA standard air density at 70 degF, 29.92 in.Hg (kg/m3).</summary>
    public const double AmcaStandardAirDensityKgM3 = 1.2;

    // ---- Length ----
    public static double MillimetersToMeters(double mm) => mm * MetersPerMillimeter;
    public static double MetersToMillimeters(double m) => m / MetersPerMillimeter;
    public static double InchesToMeters(double inches) => inches * MetersPerInch;
    public static double MetersToInches(double m) => m / MetersPerInch;
    public static double FeetToMeters(double ft) => ft * MetersPerFoot;
    public static double MetersToFeet(double m) => m / MetersPerFoot;
    public static double InchesToMillimeters(double inches) => inches * MetersPerInch / MetersPerMillimeter;
    public static double MillimetersToInches(double mm) => mm * MetersPerMillimeter / MetersPerInch;

    // ---- Volumetric flow ----
    public static double CfmToCubicMetersPerSecond(double cfm) => cfm * CubicMetersPerSecondPerCfm;
    public static double CubicMetersPerSecondToCfm(double m3s) => m3s / CubicMetersPerSecondPerCfm;
    public static double CubicMetersPerHourToCubicMetersPerSecond(double m3h) => m3h / 3600.0;
    public static double CubicMetersPerSecondToCubicMetersPerHour(double m3s) => m3s * 3600.0;
    public static double LitersPerSecondToCubicMetersPerSecond(double ls) => ls / 1000.0;
    public static double CubicMetersPerSecondToLitersPerSecond(double m3s) => m3s * 1000.0;

    // ---- Pressure ----
    public static double InchesWaterGaugeToPascals(double inWg) => inWg * PascalsPerInchWaterGauge;
    public static double PascalsToInchesWaterGauge(double pa) => pa / PascalsPerInchWaterGauge;
    public static double MmWaterGaugeToPascals(double mmWg) => mmWg * PascalsPerMmWaterGauge;
    public static double PascalsToMmWaterGauge(double pa) => pa / PascalsPerMmWaterGauge;
    public static double PsiToPascals(double psi) => psi * PascalsPerPsi;
    public static double PascalsToPsi(double pa) => pa / PascalsPerPsi;
    public static double KilopascalsToPascals(double kpa) => kpa * PascalsPerKilopascal;
    public static double PascalsToKilopascals(double pa) => pa / PascalsPerKilopascal;
    public static double BarToPascals(double bar) => bar * PascalsPerBar;
    public static double PascalsToBar(double pa) => pa / PascalsPerBar;

    // ---- Angular speed ----
    public static double RpmToRadiansPerSecond(double rpm) => rpm * 2.0 * Math.PI / 60.0;
    public static double RadiansPerSecondToRpm(double radPerSec) => radPerSec * 60.0 / (2.0 * Math.PI);

    // ---- Angle ----
    public static double DegreesToRadians(double deg) => deg * Math.PI / 180.0;
    public static double RadiansToDegrees(double rad) => rad * 180.0 / Math.PI;

    // ---- Velocity ----
    public static double FeetPerMinuteToMetersPerSecond(double fpm) => fpm * MetersPerFoot / 60.0;
    public static double MetersPerSecondToFeetPerMinute(double ms) => ms * 60.0 / MetersPerFoot;
    public static double FeetPerSecondToMetersPerSecond(double fps) => fps * MetersPerFoot;
    public static double MetersPerSecondToFeetPerSecond(double ms) => ms / MetersPerFoot;

    // ---- Power ----
    public static double HorsepowerToWatts(double hp) => hp * WattsPerHorsepower;
    public static double WattsToHorsepower(double w) => w / WattsPerHorsepower;
    public static double KilowattsToWatts(double kw) => kw * 1000.0;
    public static double WattsToKilowatts(double w) => w / 1000.0;

    // ---- Mass / density ----
    public static double PoundsToKilograms(double lb) => lb * KilogramsPerPound;
    public static double KilogramsToPounds(double kg) => kg / KilogramsPerPound;
    public static double LbPerFt3ToKgPerM3(double lbft3) => lbft3 * KgPerM3PerLbPerFt3;
    public static double KgPerM3ToLbPerFt3(double kgm3) => kgm3 / KgPerM3PerLbPerFt3;

    // ---- Temperature ----
    public static double CelsiusToKelvin(double c) => c + 273.15;
    public static double KelvinToCelsius(double k) => k - 273.15;
    public static double FahrenheitToKelvin(double f) => (f - 32.0) * 5.0 / 9.0 + 273.15;
    public static double KelvinToFahrenheit(double k) => (k - 273.15) * 9.0 / 5.0 + 32.0;

    // ---- Air density ----
    /// <summary>
    /// Ideal-gas dry air density.
    /// </summary>
    /// <param name="temperatureK">Absolute temperature in K (must be &gt; 0).</param>
    /// <param name="absolutePressurePa">Absolute pressure in Pa (must be &gt; 0).</param>
    public static double AirDensity(double temperatureK, double absolutePressurePa)
    {
        if (temperatureK <= 0.0) throw new ArgumentOutOfRangeException(nameof(temperatureK), "Temperature must be greater than 0 K.");
        if (absolutePressurePa <= 0.0) throw new ArgumentOutOfRangeException(nameof(absolutePressurePa), "Pressure must be greater than 0 Pa.");

        const double specificGasConstantAir = 287.058; // J/(kg*K)
        return absolutePressurePa / (specificGasConstantAir * temperatureK);
    }
}
