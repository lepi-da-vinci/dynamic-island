using System.Net.Http;
using System.Text.Json;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

public sealed record HourlyWeatherInfo(string HourText, double TempC, string IconKey);

public sealed record WeatherInfo(
    string City,
    double TempC,
    double ApparentTempC,
    int Humidity,
    double WindSpeedKmh,
    int WeatherCode,
    string ConditionText,
    string IconKey,
    List<HourlyWeatherInfo> HourlyForecast);

/// <summary>
/// Fetches real-time weather and hourly forecast from Open-Meteo API (free, zero API key)
/// using IP geolocation with in-memory caching.
/// </summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private WeatherInfo? _cachedWeather;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);

    private string _cachedCity = "Jakarta";
    private double _cachedLat = -6.2056;
    private double _cachedLon = 106.8376;
    private bool _locationResolved;

    public event Action<WeatherInfo>? WeatherUpdated;

    public async Task<WeatherInfo?> GetWeatherAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedWeather != null && DateTime.UtcNow - _lastFetchUtc < CacheDuration)
        {
            return _cachedWeather;
        }

        try
        {
            if (!_locationResolved)
            {
                await ResolveLocationAsync();
            }

            string url = $"https://api.open-meteo.com/v1/forecast?latitude={_cachedLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={_cachedLon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,wind_speed_10m&hourly=temperature_2m,weather_code&timezone=auto&forecast_days=1";

            using var res = await Http.GetAsync(url);
            if (!res.IsSuccessStatusCode)
            {
                return _cachedWeather;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (!root.TryGetProperty("current", out var cur))
            {
                return _cachedWeather;
            }

            double temp = cur.GetProperty("temperature_2m").GetDouble();
            double apparent = cur.GetProperty("apparent_temperature").GetDouble();
            int humidity = cur.GetProperty("relative_humidity_2m").GetInt32();
            int code = cur.GetProperty("weather_code").GetInt32();
            double wind = cur.GetProperty("wind_speed_10m").GetDouble();

            var (condText, iconKey) = MapWeatherCode(code);

            // Hourly forecast parsing (next 5 hours)
            var hourlyList = new List<HourlyWeatherInfo>();
            if (root.TryGetProperty("hourly", out var hourly) &&
                hourly.TryGetProperty("time", out var timeArr) &&
                hourly.TryGetProperty("temperature_2m", out var tempArr) &&
                hourly.TryGetProperty("weather_code", out var codeArr))
            {
                int currentHour = DateTime.Now.Hour;
                int count = timeArr.GetArrayLength();

                for (int i = 0; i < count && hourlyList.Count < 5; i++)
                {
                    string timeStr = timeArr[i].GetString() ?? string.Empty;
                    if (DateTime.TryParse(timeStr, out var parsedDt))
                    {
                        if (parsedDt.Hour >= currentHour)
                        {
                            double hTemp = tempArr[i].GetDouble();
                            int hCode = codeArr[i].GetInt32();
                            var (_, hIcon) = MapWeatherCode(hCode);
                            hourlyList.Add(new HourlyWeatherInfo(parsedDt.ToString("HH:00"), hTemp, hIcon));
                        }
                    }
                }
            }

            _cachedWeather = new WeatherInfo(
                City: _cachedCity,
                TempC: Math.Round(temp, 1),
                ApparentTempC: Math.Round(apparent, 1),
                Humidity: humidity,
                WindSpeedKmh: Math.Round(wind, 1),
                WeatherCode: code,
                ConditionText: condText,
                IconKey: iconKey,
                HourlyForecast: hourlyList);

            _lastFetchUtc = DateTime.UtcNow;
            WeatherUpdated?.Invoke(_cachedWeather);
            return _cachedWeather;
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("WeatherService", $"Failed to fetch weather: {ex.Message}");
            return _cachedWeather;
        }
    }

    private async Task ResolveLocationAsync()
    {
        try
        {
            using var res = await Http.GetAsync("http://ip-api.com/json/");
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var st) && st.GetString() == "success")
                {
                    if (root.TryGetProperty("city", out var city)) _cachedCity = city.GetString() ?? "Jakarta";
                    if (root.TryGetProperty("lat", out var lat)) _cachedLat = lat.GetDouble();
                    if (root.TryGetProperty("lon", out var lon)) _cachedLon = lon.GetDouble();
                    _locationResolved = true;
                }
            }
        }
        catch
        {
            // Default location used
            _locationResolved = true;
        }
    }

    public static (string ConditionText, string IconKey) MapWeatherCode(int code) => code switch
    {
        0 => ("Cerah", "GeomWeatherSun"),
        1 or 2 => ("Cerah Berawan", "GeomWeatherSunCloud"),
        3 => ("Berawan", "GeomWeatherCloud"),
        45 or 48 => ("Berkabut", "GeomWeatherCloud"),
        51 or 53 or 55 => ("Gerimis", "GeomWeatherRain"),
        61 or 63 or 65 => ("Hujan", "GeomWeatherRain"),
        71 or 73 or 75 => ("Hujan Salju", "GeomWeatherRain"),
        80 or 81 or 82 => ("Hujan Lebat", "GeomWeatherRain"),
        95 or 96 or 99 => ("Badai Petir", "GeomWeatherRain"),
        _ => ("Sebagian Berawan", "GeomWeatherSunCloud")
    };
}
