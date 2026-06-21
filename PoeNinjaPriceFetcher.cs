using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RitualHelper
{
    public class PoeNinjaPrice
    {
        public double Price { get; set; }
        public string Currency { get; set; }
        public double? MaxVolumeRate { get; set; }
        public string MaxVolumeCurrency { get; set; }
        public double? TotalChange { get; set; }
        public double? Volume { get; set; }

        public string ExchangeRateDisplay { get; set; }
        public string ChangePercentDisplay { get; set; }
        public string VolumeDisplay { get; set; }
    }

    public static class PoeNinjaPriceFetcher
    {
        private static Dictionary<string, PoeNinjaPrice> priceCache = new Dictionary<string, PoeNinjaPrice>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, PoeNinjaPrice> artPriceCache = new Dictionary<string, PoeNinjaPrice>(StringComparer.OrdinalIgnoreCase);
        private static bool isFetching = false;
        private static string cacheFilePath;
        public static double DivineToExaltedRate { get; private set; } = 80.0; // default fallback

        /// <summary>Returns a display-ready (value, currency) pair. If price < 1 Div, converts to Ex.</summary>
        public static (double Value, string Currency) GetDisplayPrice(PoeNinjaPrice price)
        {
            if (price.Currency == "divine" && price.Price < 1.0)
            {
                return (Math.Round(price.Price * DivineToExaltedRate, 1), "ex");
            }
            return (Math.Round(price.Price, 1), price.Currency);
        }

        public static void Initialize(string pluginDirectory)
        {
            cacheFilePath = Path.Combine(pluginDirectory, "ninja_prices.json");

            // Always delete and re-fetch on startup for fresh prices
            if (File.Exists(cacheFilePath))
            {
                try { File.Delete(cacheFilePath); } catch { }
            }

            priceCache.Clear();
            artPriceCache.Clear();

            if (!isFetching)
            {
                isFetching = true;
                Task.Run(FetchPricesAsync);
            }
        }

        public static PoeNinjaPrice GetPrice(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return null;
            if (priceCache.TryGetValue(itemName, out var price))
                return price;
            return null;
        }

        public static PoeNinjaPrice GetPriceByArt(string artName)
        {
            if (string.IsNullOrEmpty(artName)) return null;
            if (artPriceCache.TryGetValue(artName, out var price))
                return price;
            return null;
        }

        private static string ArtBasenameFromIcon(string iconUrl)
        {
            if (string.IsNullOrWhiteSpace(iconUrl)) return null;
            var noQuery = iconUrl.Split('?')[0];
            var seg = System.Linq.Enumerable.LastOrDefault(noQuery.Split('/'));
            if (string.IsNullOrWhiteSpace(seg)) return null;
            var dot = seg.LastIndexOf('.');
            var name = dot > 0 ? seg[..dot] : seg;
            return name.Length >= 2 ? name : null;
        }

        private static void LoadFromCache()
        {
            try
            {
                if (File.Exists(cacheFilePath))
                {
                    var json = File.ReadAllText(cacheFilePath);
                    var cached = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, PoeNinjaPrice>>(json);
                    if (cached != null)
                    {
                        foreach (var kvp in cached)
                        {
                            priceCache[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch { }
        }

        private static async Task FetchPricesAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "RitualHelper-GameHelper-Plugin");

                    // Exchange API (Ritual/Omens): primaryValue is in core.primary currency (divine)
                    await FetchExchangeApi(client,
                        "https://poe.ninja/poe2/api/economy/exchange/current/overview?league=Runes+of+Aldur&type=Ritual");

                    // Stash APIs (Uniques): primaryValue is in Exalted Orbs
                    var stashUrls = new[]
                    {
                        "https://poe.ninja/poe2/api/economy/stash/current/item/overview?league=Runes+of+Aldur&type=UniqueArmours",
                        "https://poe.ninja/poe2/api/economy/stash/current/item/overview?league=Runes+of+Aldur&type=UniqueAccessories",
                        "https://poe.ninja/poe2/api/economy/stash/current/item/overview?league=Runes+of+Aldur&type=UniqueCharms",
                        "https://poe.ninja/poe2/api/economy/stash/current/item/overview?league=Runes+of+Aldur&type=UniqueWeapons",
                    };

                    foreach (var url in stashUrls)
                    {
                        await FetchStashApi(client, url);
                    }

                    // Save to JSON cache
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(priceCache, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(cacheFilePath, json);
                }
            }
            catch { }
            finally
            {
                isFetching = false;
            }
        }

        // Exchange API: core.primary is the currency for primaryValue (e.g. "divine")
        private static async Task FetchExchangeApi(HttpClient client, string url)
        {
            try
            {
                var response = await client.GetStringAsync(url);
                var data = JObject.Parse(response);

                // Determine the primary currency from core block
                var primaryCurrency = data["core"]?["primary"]?.ToString() ?? "divine";

                // Store the divine-to-exalted rate for display conversion
                var rateToken = data["core"]?["rates"]?["exalted"];
                if (rateToken != null)
                {
                    var rate = rateToken.Value<double>();
                    if (rate > 0) DivineToExaltedRate = rate;
                }

                // Build id -> name map from items array
                var idToMeta = new Dictionary<string, (string Name, string Art)>();
                var itemsArray = data["items"] as JArray;
                if (itemsArray != null)
                {
                    foreach (var item in itemsArray)
                    {
                        var id = item["id"]?.ToString();
                        var name = item["name"]?.ToString();
                        if (id != null && name != null)
                        {
                            var art = ArtBasenameFromIcon(item["image"]?.ToString());
                            idToMeta[id] = (name, art);
                        }
                    }
                }

                var lines = data["lines"] as JArray;
                if (lines == null) return;

                foreach (var line in lines)
                {
                    var id = line["id"]?.ToString();
                    if (id == null) continue;

                    if (!idToMeta.TryGetValue(id, out var meta)) continue;
                    var name = meta.Name;

                    double price = line["primaryValue"]?.Value<double>() ?? 0.0;
                    if (price <= 0) continue;

                    double? maxVolumeRate = line["maxVolumeRate"]?.Value<double>();
                    string maxVolumeCurrency = line["maxVolumeCurrency"]?.ToString();
                    double? totalChange = line["sparkline"]?["totalChange"]?.Value<double>();
                    double? volume = line["volumePrimaryValue"]?.Value<double>();

                    string exchangeRateDisplay = null;
                    if (maxVolumeRate.HasValue && !string.IsNullOrEmpty(maxVolumeCurrency))
                    {
                        exchangeRateDisplay = $"1.0 = {maxVolumeRate.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} {maxVolumeCurrency}";
                    }

                    string changePercentDisplay = null;
                    if (totalChange.HasValue)
                    {
                        int roundedChange = (int)Math.Round(totalChange.Value, MidpointRounding.AwayFromZero);
                        changePercentDisplay = $"{roundedChange}%";
                    }

                    string volumeDisplay = null;
                    if (volume.HasValue)
                    {
                        int roundedVolume = (int)Math.Round(volume.Value, MidpointRounding.AwayFromZero);
                        volumeDisplay = roundedVolume.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }

                    var newPrice = new PoeNinjaPrice
                    {
                        Price = price,
                        Currency = primaryCurrency,
                        MaxVolumeRate = maxVolumeRate,
                        MaxVolumeCurrency = maxVolumeCurrency,
                        TotalChange = totalChange,
                        Volume = volume,
                        ExchangeRateDisplay = exchangeRateDisplay,
                        ChangePercentDisplay = changePercentDisplay,
                        VolumeDisplay = volumeDisplay
                    };

                    priceCache[name] = newPrice;
                    if (meta.Art != null)
                    {
                        artPriceCache[meta.Art] = newPrice;
                    }
                }
            }
            catch { }
        }

        // Stash API: primaryValue is in the primary currency returned in core.primary (usually divine)
        private static async Task FetchStashApi(HttpClient client, string url)
        {
            try
            {
                var response = await client.GetStringAsync(url);
                var data = JObject.Parse(response);

                // Determine primary currency and rates from core block if present
                var primaryCurrency = data["core"]?["primary"]?.ToString() ?? "divine";
                var rateToken = data["core"]?["rates"]?["exalted"];
                if (rateToken != null)
                {
                    var rate = rateToken.Value<double>();
                    if (rate > 0) DivineToExaltedRate = rate;
                }

                var lines = data["lines"] as JArray;
                if (lines == null) return;

                foreach (var line in lines)
                {
                    string name = line["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    string baseType = line["baseType"]?.ToString() ?? "";

                    double price = line["primaryValue"]?.Value<double>() ?? 0.0;
                    if (price <= 0) continue;

                    double? maxVolumeRate = line["maxVolumeRate"]?.Value<double>();
                    string maxVolumeCurrency = line["maxVolumeCurrency"]?.ToString();
                    double? totalChange = (line["sparkline"] ?? line["sparkLine"])?["totalChange"]?.Value<double>();
                    double? volume = line["volumePrimaryValue"]?.Value<double>();

                    string exchangeRateDisplay = null;
                    if (maxVolumeRate.HasValue && !string.IsNullOrEmpty(maxVolumeCurrency))
                    {
                        exchangeRateDisplay = $"1.0 = {maxVolumeRate.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} {maxVolumeCurrency}";
                    }

                    string changePercentDisplay = null;
                    if (totalChange.HasValue)
                    {
                        int roundedChange = (int)Math.Round(totalChange.Value, MidpointRounding.AwayFromZero);
                        changePercentDisplay = $"{roundedChange}%";
                    }

                    string volumeDisplay = null;
                    if (volume.HasValue)
                    {
                        int roundedVolume = (int)Math.Round(volume.Value, MidpointRounding.AwayFromZero);
                        volumeDisplay = roundedVolume.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }

                    var newPrice = new PoeNinjaPrice
                    {
                        Price = price,
                        Currency = primaryCurrency,
                        MaxVolumeRate = maxVolumeRate,
                        MaxVolumeCurrency = maxVolumeCurrency,
                        TotalChange = totalChange,
                        Volume = volume,
                        ExchangeRateDisplay = exchangeRateDisplay,
                        ChangePercentDisplay = changePercentDisplay,
                        VolumeDisplay = volumeDisplay
                    };

                    // Determine the cache key based on whether it is Runeforged or Runemastered
                    string cacheKey = name;
                    if (baseType.Contains("Runeforged", StringComparison.OrdinalIgnoreCase))
                    {
                        cacheKey = name + " Runeforged";
                    }
                    else if (baseType.Contains("Runemastered", StringComparison.OrdinalIgnoreCase))
                    {
                        cacheKey = name + " Runemastered";
                    }

                    // Only write if not already set (keeps the highest-priced variant)
                    if (!priceCache.ContainsKey(cacheKey))
                    {
                        priceCache[cacheKey] = newPrice;
                    }
                    else if (priceCache[cacheKey].Price < price)
                    {
                        priceCache[cacheKey] = newPrice;
                    }

                    var art = ArtBasenameFromIcon(line["icon"]?.ToString());
                    if (art != null)
                    {
                        if (!artPriceCache.ContainsKey(art))
                        {
                            artPriceCache[art] = newPrice;
                        }
                        else if (artPriceCache[art].Price < price)
                        {
                            artPriceCache[art] = newPrice;
                        }
                    }
                }
            }
            catch { }
        }
    }
}
