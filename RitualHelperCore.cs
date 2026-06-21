// <copyright file="RitualHelperCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RitualHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Reflection;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Objects.States.InGameState;
    using GameOffsets.Objects.UiElement;
    using GameOffsets.Natives;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    /// <see cref="RitualHelperCore"/> plugin.
    /// Reads item names from the Ritual shop (Favours window)
    /// and displays them in an overlay.
    /// </summary>
    public sealed class RitualHelperCore : PCore<RitualHelperSettings>
    {
        // Rarity colors (ABGR format for ImGui)
        private static readonly Vector4 ColorNormalV4 = new(1f, 1f, 1f, 1f);
        private static readonly Vector4 ColorMagicV4 = new(0.52f, 0.52f, 1f, 1f);
        private static readonly Vector4 ColorRareV4 = new(1f, 1f, 0.35f, 1f);
        private static readonly Vector4 ColorUniqueV4 = new(1f, 0.45f, 0f, 1f);
        private static readonly Vector4 ColorCurrencyV4 = new(0.67f, 1f, 0.5f, 1f);
        private static readonly Vector4 ColorGemV4 = new(0.2f, 0.8f, 0.8f, 1f);
        private static readonly Vector4 ColorHeaderV4 = new(1f, 0.84f, 0f, 1f);


        
        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.Checkbox("Show Ritual Overlay", ref this.Settings.ShowOverlay);
            ImGui.Checkbox("Debug Mode (Show All Inventories)", ref this.Settings.DebugMode);
            ImGui.Separator();
            ImGui.TextWrapped(
                "This plugin uses a signature-based BFS scan to locate the post-ritual tribute shop " +
                "and displays prices below each item. Open the Ritual window in-game for items to appear.");
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (!this.Settings.ShowOverlay && !this.Settings.DebugMode)
            {
                return;
            }

            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            try
            {
                string currentClipboard = "";
                try { currentClipboard = ImGui.GetClipboardText() ?? ""; } catch { }
                bool clipboardChanged = false;
                
                if (currentClipboard != this.lastClipboardText && currentClipboard.StartsWith("Item Class:"))
                {
                    clipboardChanged = true;
                    this.lastClipboardText = currentClipboard;
                }


                var uiRoot = Core.States.InGameStateObject.GameUi.Address;
                if (uiRoot != IntPtr.Zero)
                {
                    var sigEl = FindSignatureElement(uiRoot);
                    if (sigEl != IntPtr.Zero)
                    {
                        var cur = sigEl;
                        IntPtr grid = IntPtr.Zero;
                        for (var up = 0; up < 8 && grid == IntPtr.Zero; up++)
                        {
                            grid = FindRewardGrid(cur);
                            var parent = Ptr(cur + 0xB8); // Parent offset is 0xB8
                            if (parent == IntPtr.Zero) break;
                            cur = parent;
                        }

                        if (grid != IntPtr.Zero && ChildSpan(grid, out var gf, out var gn))
                        {
                            var fgDraw = ImGui.GetForegroundDrawList();
                            float winW = Core.Process.WindowArea.Width;
                            float winH = Core.Process.WindowArea.Height;

                            for (long i = 0; i < gn; i++)
                            {
                                var tile = Ptr(gf + (int)(i * 8));
                                var item = TileItem(tile);
                                if (item == IntPtr.Zero) continue;

                                var identity = ReadIdentityFromItem(item);
                                if (!TryUiElementRect(tile, winW, winH, out var tx, out var ty, out var tw, out var th)) continue;

                                PoeNinjaPrice priceInfo = null;
                                string itemName = "";
                                bool isMapped = false;

                                if (identity.Rarity == Rarity.Unique)
                                {
                                    if (!string.IsNullOrEmpty(identity.Art))
                                    {
                                        priceInfo = PoeNinjaPriceFetcher.GetPriceByArt(identity.Art);
                                    }
                                    if (priceInfo == null && !string.IsNullOrEmpty(identity.InternalNameOnly))
                                    {
                                        itemName = this.GetPrettyName(identity.InternalNameOnly, out isMapped);
                                        priceInfo = PoeNinjaPriceFetcher.GetPrice(itemName);
                                    }
                                }
                                else
                                {
                                    if (!string.IsNullOrEmpty(identity.InternalNameOnly))
                                    {
                                        itemName = this.GetPrettyName(identity.InternalNameOnly, out isMapped);
                                        priceInfo = PoeNinjaPriceFetcher.GetPrice(itemName);
                                    }
                                    if (priceInfo == null && !string.IsNullOrEmpty(identity.Name))
                                    {
                                        priceInfo = PoeNinjaPriceFetcher.GetPrice(identity.Name);
                                    }
                                }

                                // Clipboard mapping
                                if (clipboardChanged && !string.IsNullOrEmpty(identity.InternalNameOnly))
                                {
                                    var mousePos = ImGui.GetMousePos();
                                    if (mousePos.X >= tx && mousePos.X <= tx + tw &&
                                        mousePos.Y >= ty && mousePos.Y <= ty + th)
                                    {
                                        var parsedName = this.ParseClipboardForItemName(currentClipboard);
                                        if (!string.IsNullOrEmpty(parsedName))
                                        {
                                            this.UpdateCustomName(identity.InternalNameOnly, parsedName);
                                            itemName = parsedName;
                                            priceInfo = PoeNinjaPriceFetcher.GetPrice(itemName);
                                        }
                                    }
                                }

                                string label = "";
                                uint color = 0xFFFFFFFF; // White
                                if (priceInfo != null)
                                {
                                    var (displayValue, displayCurrency) = PoeNinjaPriceFetcher.GetDisplayPrice(priceInfo);
                                    if (displayCurrency == "divine")
                                    {
                                        label = $"{displayValue:0.##} Div";
                                        color = 0xFFFFFFFF;
                                    }
                                    else if (displayCurrency == "exalted" || displayCurrency == "ex")
                                    {
                                        label = $"{displayValue:0.##} Ex";
                                        color = 0xFF00D7FF; // Gold
                                    }
                                    else
                                    {
                                        label = $"{displayValue:0.##} C";
                                        color = 0xFF00D7FF; // Gold/yellow
                                    }
                                }

                                var rectMin = new Vector2(tx, ty);
                                var rectMax = new Vector2(tx + tw, ty + th);

                                // Border color by rarity
                                uint borderCol = 0xFFFFFFFF;
                                if (identity.Rarity == Rarity.Unique) borderCol = 0xFF007CFF; // Unique (Orange)
                                else if (identity.Rarity == Rarity.Rare) borderCol = 0xFF00FFFF; // Rare (Yellow)
                                else if (identity.Rarity == Rarity.Magic) borderCol = 0xFFFF8F00; // Magic (Blue)

                                fgDraw.AddRect(rectMin, rectMax, borderCol, 0, ImDrawFlags.None, 1.5f);

                                if (!string.IsNullOrEmpty(label))
                                {
                                    var pSize = ImGui.CalcTextSize(label);
                                    float chipH = pSize.Y + 4f;
                                    float chipW = pSize.X + 8f;
                                    float chipX = tx + (tw - chipW) / 2f;
                                    float chipY = ty + th - chipH / 2f;

                                    var chipMin = new Vector2(chipX, chipY);
                                    var chipMax = new Vector2(chipX + chipW, chipY + chipH);

                                    fgDraw.AddRectFilled(chipMin, chipMax, 0xDD151515, 3f);
                                    fgDraw.AddRect(chipMin, chipMax, borderCol, 3f, ImDrawFlags.None, 1f);
                                    fgDraw.AddText(chipMin + new Vector2(4f, 2f), color, label);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            if (this.Settings.DebugMode)
            {
                this.DrawDebugOverlay();
            }
        }

        private struct ItemIdentity
        {
            public Rarity Rarity;
            public string Art;
            public string Name;
            public bool Identified;
            public string Path;
            public string InternalNameOnly;
        }

        private static IntPtr Ptr(IntPtr addr)
        {
            var reader = Core.Process.Handle;
            if (!reader.TryReadMemory<IntPtr>(addr, out var p)) return IntPtr.Zero;
            var u = (ulong)p;
            return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? IntPtr.Zero : p;
        }

        private static string ReadStdWString(IntPtr addr)
        {
            var reader = Core.Process.Handle;
            if (!reader.TryReadMemory<StdWString>(addr, out var wstr)) return string.Empty;
            return reader.ReadStdWString(wstr);
        }

        private static string ReadMetadata(IntPtr entity)
        {
            var details = Ptr(entity + 0x08);
            if (details == IntPtr.Zero) return string.Empty;
            return ReadStdWString(details + 0x08);
        }

        private static string ArtBasename(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var lastSlash = path.LastIndexOf('/');
            var name = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
            var dot = name.IndexOf('.');
            return dot >= 0 ? name[..dot] : name;
        }

        private static IntPtr ResolveComponent(IntPtr entity, string name)
        {
            var reader = Core.Process.Handle;
            var details = Ptr(entity + 0x08);
            if (details == IntPtr.Zero) return IntPtr.Zero;
            var lookup = Ptr(details + 0x28);
            if (lookup == IntPtr.Zero) return IntPtr.Zero;
            var compList = reader.ReadMemory<StdVector>(entity + 0x10);
            var compCount = ((long)compList.Last - (long)compList.First) / 8;
            if (compCount <= 0 || compCount > 256) return IntPtr.Zero;

            var bFirst = Ptr(lookup + 0x28);
            if (!reader.TryReadMemory<IntPtr>(lookup + 0x30, out var bLast)) return IntPtr.Zero;
            var entries = ((long)bLast - (long)bFirst) / 16;
            if (bFirst == IntPtr.Zero || entries <= 0 || entries > 256) return IntPtr.Zero;

            for (long i = 0; i < entries; i++)
            {
                var e = bFirst + (int)(i * 16);
                var namePtr = Ptr(e);
                if (!reader.TryReadMemory<int>(e + 8, out var index)) continue;
                if (index < 0 || index >= compCount) continue;
                var compName = reader.ReadString(namePtr);
                if (compName != name) continue;
                return Ptr(compList.First + (int)(index * 8));
            }
            return IntPtr.Zero;
        }

        private static ItemIdentity ReadIdentityFromItem(IntPtr item)
        {
            var identity = new ItemIdentity
            {
                Rarity = Rarity.Normal,
                Art = null,
                Name = null,
                Identified = true,
                Path = "",
                InternalNameOnly = ""
            };

            if (item == IntPtr.Zero) return identity;

            identity.Path = ReadMetadata(item);
            if (!string.IsNullOrEmpty(identity.Path))
            {
                var parts = identity.Path.Split('/');
                if (parts.Length > 0)
                {
                    identity.InternalNameOnly = parts[parts.Length - 1];
                }
            }

            var modsComp = ResolveComponent(item, "Mods");
            if (modsComp != IntPtr.Zero)
            {
                var reader = Core.Process.Handle;
                if (reader.TryReadMemory<byte>(modsComp + 0x90, out var idf))
                {
                    identity.Identified = idf != 0;
                }
                if (reader.TryReadMemory<int>(modsComp + 0x94, out var r))
                {
                    if (r >= 0 && r <= 3)
                    {
                        identity.Rarity = (Rarity)r;
                    }
                }
            }

            var renderItem = ResolveComponent(item, "RenderItem");
            if (renderItem != IntPtr.Zero)
            {
                var pathPtr = Ptr(renderItem + 0x28);
                if (pathPtr != IntPtr.Zero)
                {
                    var fullPath = Core.Process.Handle.ReadUnicodeString(pathPtr);
                    identity.Art = ArtBasename(fullPath);
                }
            }

            var baseComp = ResolveComponent(item, "Base");
            if (baseComp != IntPtr.Zero)
            {
                var nameRow = Ptr(baseComp + 0x10);
                var namePtr = nameRow == IntPtr.Zero ? IntPtr.Zero : Ptr(nameRow + 0x30);
                if (namePtr != IntPtr.Zero)
                {
                    var s = Core.Process.Handle.ReadUnicodeString(namePtr);
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        identity.Name = s.Trim();
                    }
                }
            }

            return identity;
        }

        private static bool ChildSpan(IntPtr el, out IntPtr first, out long n)
        {
            var reader = Core.Process.Handle;
            first = Ptr(el + 0x10);
            n = 0;
            if (first == IntPtr.Zero) return false;
            if (!reader.TryReadMemory<IntPtr>(el + 0x18, out var last)) return false;
            n = ((long)last - (long)first) / 8;
            return n > 0 && n <= 4000;
        }

        private static IntPtr TileItem(IntPtr tile)
        {
            if (tile == IntPtr.Zero) return IntPtr.Zero;
            var item = Ptr(tile + 0x4F8);
            return item != IntPtr.Zero && ResolveComponent(item, "RenderItem") != IntPtr.Zero ? item : IntPtr.Zero;
        }

        private static IntPtr FindRewardGrid(IntPtr parent)
        {
            if (!ChildSpan(parent, out var first, out var n)) return IntPtr.Zero;
            IntPtr best = IntPtr.Zero;
            var bestItems = 0;
            for (long i = 0; i < n; i++)
            {
                var c = Ptr(first + (int)(i * 8));
                if (!ChildSpan(c, out var cf, out var cn) || cn < 1 || cn > 120) continue;
                var items = 0;
                for (long k = 0; k < cn; k++)
                {
                    if (TileItem(Ptr(cf + (int)(k * 8))) != IntPtr.Zero) items++;
                }
                if (items >= 2 && items > bestItems)
                {
                    best = c;
                    bestItems = items;
                }
            }
            return best;
        }

        private static IntPtr FindSignatureElement(IntPtr uiRoot)
        {
            if (uiRoot == IntPtr.Zero) return IntPtr.Zero;
            var reader = Core.Process.Handle;

            var queue = new Queue<IntPtr>();
            queue.Enqueue(uiRoot);
            var visited = new HashSet<IntPtr>();
            while (queue.Count > 0 && visited.Count < 20000)
            {
                var el = queue.Dequeue();
                if (el == IntPtr.Zero || !visited.Add(el)) continue;

                var visible = reader.TryReadMemory<uint>(el + 0x180, out var flags) && (flags & (1u << 0x0B)) != 0;
                if (!visible && el != uiRoot) continue; // prune invisible subtree

                if (ChildSpan(el, out var f, out var nn))
                {
                    for (long k = 0; k < nn; k++)
                    {
                        queue.Enqueue(Ptr(f + (int)(k * 8)));
                    }
                }

                var textAddr = el + 0x390;
                if (reader.TryReadMemory<StdWString>(textAddr, out var wstr) && wstr.Length >= 6)
                {
                    var t = reader.ReadStdWString(wstr);
                    if (t.Contains("Rituals Remaining", StringComparison.OrdinalIgnoreCase) ||
                        t.Contains("tribute to the king", StringComparison.OrdinalIgnoreCase))
                    {
                        return el;
                    }
                }
            }
            return IntPtr.Zero;
        }

        private static bool TryUiElementRect(IntPtr el, float winW, float winH, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0f;
            if (el == IntPtr.Zero) return false;
            var reader = Core.Process.Handle;
            if (!reader.TryReadMemory<uint>(el + 0x180, out var flags)) return false;
            if ((flags & (1u << 0x0B)) == 0) return false;

            if (!reader.TryReadMemory<byte>(el + 0x18A, out var idx)) return false;
            reader.TryReadMemory<float>(el + 0x130, out var mul);
            reader.TryReadMemory<Vector2>(el + 0x288, out var sz);

            var (sw, sh) = UiScaleValue(idx, mul, winW, winH);
            if (sw <= 0f || sh <= 0f) return false;
            var (px, py) = UiUnscaledPos(el, 0, winW, winH);
            if (!float.IsFinite(px) || !float.IsFinite(py)) return false;
            x = px * sw; y = py * sh; w = sz.X * sw; h = sz.Y * sh;
            return w > 1f && h > 1f;
        }

        private static (float w, float h) UiScaleValue(byte idx, float mul, float winW, float winH)
        {
            if (mul == 0f) mul = 1f;
            var v1 = winW / 2560.0f;
            var v2 = winH / 1600.0f;
            float w = mul, h = mul;
            switch (idx)
            {
                case 1: w *= v1; h *= v1; break;
                case 2: w *= v2; h *= v2; break;
                case 3: w *= v1; h *= v2; break;
            }
            return (w, h);
        }

        private static (float x, float y) UiUnscaledPos(IntPtr el, int depth, float winW, float winH)
        {
            var reader = Core.Process.Handle;
            reader.TryReadMemory<Vector2>(el + 0x118, out var rel);
            var parent = Ptr(el + 0xB8);
            if (parent == IntPtr.Zero || depth >= 64) return (rel.X, rel.Y);

            var (ppx, ppy) = UiUnscaledPos(parent, depth + 1, winW, winH);

            if (reader.TryReadMemory<uint>(el + 0x180, out var flags) && (flags & (1u << 0x0A)) != 0)
            {
                reader.TryReadMemory<Vector2>(el + 0xF0, out var mod);
                ppx += mod.X; ppy += mod.Y;
            }
            return (ppx + rel.X, ppy + rel.Y);
        }
        private string lastClipboardText = "";

        private string ParseClipboardForItemName(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith("Item Class:")) return null;
            
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 2)
            {
                return lines[2].Trim();
            }
            return null;
        }

        private void UpdateCustomName(string internalName, string newName)
        {
            if (this.customNamesCache == null) return;

            string baseInternalName = internalName;
            if (internalName.EndsWith("Runeforged", StringComparison.OrdinalIgnoreCase))
            {
                baseInternalName = internalName.Substring(0, internalName.Length - "Runeforged".Length);
            }
            else if (internalName.EndsWith("Runemastered", StringComparison.OrdinalIgnoreCase))
            {
                baseInternalName = internalName.Substring(0, internalName.Length - "Runemastered".Length);
            }
            else if (internalName.EndsWith("reforged", StringComparison.OrdinalIgnoreCase))
            {
                baseInternalName = internalName.Substring(0, internalName.Length - "reforged".Length);
            }

            this.customNamesCache[baseInternalName] = newName;
            var dictionaryPath = System.IO.Path.Combine(this.DllDirectory, "item_names.json");
            try 
            {
                System.IO.File.WriteAllText(dictionaryPath, Newtonsoft.Json.JsonConvert.SerializeObject(this.customNamesCache, Newtonsoft.Json.Formatting.Indented));
            } catch { }
        }
        private System.Collections.Generic.Dictionary<string, string> customNamesCache = null;

        private string GetPrettyName(string internalName, out bool isMapped)
        {
            isMapped = false;
            var dictionaryPath = System.IO.Path.Combine(this.DllDirectory, "item_names.json");
            
            if (this.customNamesCache == null)
            {
                if (System.IO.File.Exists(dictionaryPath))
                {
                    try
                    {
                        var content = System.IO.File.ReadAllText(dictionaryPath);
                        this.customNamesCache = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(content);
                    }
                    catch { }
                }

                if (this.customNamesCache == null)
                {
                    this.customNamesCache = new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "FourBelt3", "Wide Belt" },
                        { "FourShieldStrDex4", "AdicioneONomeDoShieldAqui" },
                        { "OmenGambleNoGoldCost", "Omen of Gambling" },
                        { "HiddenItem1x1Ritual", "Hidden Ritual Item (1x1)" },
                        { "HiddenItem1x2Ritual", "Hidden Ritual Item (1x2)" },
                        { "HiddenItem1x3Ritual", "Hidden Ritual Item (1x3)" },
                        { "HiddenItem1x4Ritual", "Hidden Ritual Item (1x4)" },
                        { "HiddenItem2x2Ritual", "Hidden Ritual Item (2x2)" },
                        { "HiddenItem2x3Ritual", "Hidden Ritual Item (2x3)" },
                        { "HiddenItem2x4Ritual", "Hidden Ritual Item (2x4)" },
                        { "FourCharm3", "Charm" },
                        { "FourBodyStr6", "Strength Body Armour" },
                        { "FourWand3", "Wand" },
                        { "FourQuarterstaff5", "Quarterstaff" },
                        { "FourStaff10", "Staff" },
                        { "DistilledEmotion6", "Distilled Emotion" },
                        { "CurrencyUpgradeToMagic3", "Orb of Transmutation" },
                        { "MapKeyTier15", "Tier 15 Map" },
                        { "FourRing3", "Ring" },
                        { "ReservationGemUncut18", "Uncut Reservation Gem" },
                        { "CurrencyArmourQuality", "Armourer's Scrap" },
                        { "FourGlovesStr6", "Strength Gloves" }
                    };
                    
                    try 
                    {
                        System.IO.File.WriteAllText(dictionaryPath, Newtonsoft.Json.JsonConvert.SerializeObject(this.customNamesCache, Newtonsoft.Json.Formatting.Indented));
                    } catch { }
                }
            }

            string baseInternalName = internalName;
            string suffix = "";
            
            if (internalName.EndsWith("Runeforged", StringComparison.OrdinalIgnoreCase))
            {
                baseInternalName = internalName.Substring(0, internalName.Length - "Runeforged".Length);
                suffix = "Runeforged";
            }
            else if (internalName.EndsWith("Runemastered", StringComparison.OrdinalIgnoreCase))
            {
                baseInternalName = internalName.Substring(0, internalName.Length - "Runemastered".Length);
                suffix = "Runemastered";
            }
            else if (internalName.EndsWith("reforged", StringComparison.OrdinalIgnoreCase))
            {
                baseInternalName = internalName.Substring(0, internalName.Length - "reforged".Length);
                suffix = "Runeforged";
            }

            if (this.customNamesCache.TryGetValue(baseInternalName, out var pretty))
            {
                isMapped = true;
                return string.IsNullOrEmpty(suffix) ? pretty : $"{pretty} {suffix}";
            }

            var clean = System.Text.RegularExpressions.Regex.Replace(internalName, "([A-Z])", " $1").Trim();
            clean = clean.Replace("Four ", "");
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\d+", "").Trim();
            return clean;
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                try
                {
                    var content = File.ReadAllText(this.SettingPathname);
                    this.Settings = JsonConvert.DeserializeObject<RitualHelperSettings>(content)
                        ?? new RitualHelperSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RitualHelper] Failed to load settings: {ex.Message}");
                    this.Settings = new RitualHelperSettings();
                }
            }

            // Always fetch/initialize PoeNinja prices when plugin is loaded/enabled
            PoeNinjaPriceFetcher.Initialize(this.DllDirectory);
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
                var settingsData = JsonConvert.SerializeObject(this.Settings, Formatting.Indented);
                File.WriteAllText(this.SettingPathname, settingsData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RitualHelper] Failed to save settings: {ex.Message}");
            }
        }

        private void DrawDebugOverlay()
        {
            ImGui.SetNextWindowSize(new Vector2(400, 300), ImGuiCond.FirstUseEver);
            var flags = ImGuiWindowFlags.NoFocusOnAppearing;
            if (ImGui.Begin("Ritual Debug##RitualHelperDebug", ref this.Settings.DebugMode, flags))
            {
                var uiManagerPtr = Core.States.InGameStateObject.GameUi.Address;
                ImGui.Text($"UiManager Address: 0x{uiManagerPtr.ToInt64():X}");
                
                var uiRoot = uiManagerPtr;
                ImGui.Text($"UiRoot Address: 0x{uiRoot.ToInt64():X}");
                
                if (uiRoot != IntPtr.Zero)
                {
                    var sigEl = FindSignatureElement(uiRoot);
                    ImGui.Text($"Signature Element: 0x{sigEl.ToInt64():X}");
                    
                    if (sigEl != IntPtr.Zero)
                    {
                        var cur = sigEl;
                        IntPtr grid = IntPtr.Zero;
                        for (var up = 0; up < 8 && grid == IntPtr.Zero; up++)
                        {
                            grid = FindRewardGrid(cur);
                            var parent = Ptr(cur + 0xB8);
                            if (parent == IntPtr.Zero) break;
                            cur = parent;
                        }
                        ImGui.Text($"Reward Grid Element: 0x{grid.ToInt64():X}");
                        
                        if (grid != IntPtr.Zero && ChildSpan(grid, out var gf, out var gn))
                        {
                            ImGui.Text($"Grid Children Count: {gn}");
                            var itemsFound = 0;
                            for (long i = 0; i < gn; i++)
                            {
                                var tile = Ptr(gf + (int)(i * 8));
                                var item = TileItem(tile);
                                if (item != IntPtr.Zero) itemsFound++;
                            }
                            ImGui.Text($"Grid Items Found: {itemsFound}");
                        }
                    }
                }
            }
            ImGui.End();
        }
    }
}
