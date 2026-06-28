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
    using System.Text;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.Utils;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States;
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

        private static int debugVisitedCount = 0;
        private static string debugLastSigSearchStatus = "Not scanned yet";
        private static readonly List<string> debugSampleTexts = new();
        
        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");

        private IntPtr scannedGridAddr = IntPtr.Zero;
        private IntPtr lastUiRoot = IntPtr.Zero;
        private DateTime nextScanUtc = DateTime.MinValue;

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.Checkbox("Show Ritual Overlay", ref this.Settings.ShowOverlay);
            ImGui.Checkbox("Debug Mode (Show All Inventories)", ref this.Settings.DebugMode);
            ImGui.Checkbox("Force BFS window search (testing)", ref this.Settings.ForceBfsFallback);
            ImGui.Separator();
            ImGui.Checkbox("Draw Ritual Wisp Range Circle", ref this.Settings.DrawWispCircle);
            if (this.Settings.DrawWispCircle)
            {
                ImGui.Indent();
                ImGui.Checkbox("Hide Wisp Circle when game is in background or paused", ref this.Settings.HideWispCircleInBackgroundOrPaused);
                ImGui.DragFloat("Circle Radius (Meters)##WispRadius", ref this.Settings.WispCircleRadiusMeters, 0.1f, 0.5f, 10f, "%.1f m");
                ImGui.DragFloat("Circle Thickness##WispThickness", ref this.Settings.WispCircleThickness, 0.1f, 0.5f, 10f, "%.1f px");
                
                var colInside = this.Settings.WispCircleColorInside;
                if (ImGui.ColorEdit4("Color (Inside Range)##WispInsideCol", ref colInside))
                {
                    this.Settings.WispCircleColorInside = colInside;
                }
                
                var colOutside = this.Settings.WispCircleColorOutside;
                if (ImGui.ColorEdit4("Color (Outside Range)##WispOutsideCol", ref colOutside))
                {
                    this.Settings.WispCircleColorOutside = colOutside;
                }
                ImGui.Unindent();
            }
            ImGui.Separator();
            ImGui.TextWrapped(
                "This plugin uses a signature-based BFS scan to locate the post-ritual tribute shop " +
                "and displays prices below each item. Open the Ritual window in-game for items to appear.");
            if (this.Settings.DebugMode)
            {
                ImGui.Separator();
                ImGui.Text($"UI Root Address: 0x{Core.States.InGameStateObject.GameUi.Address.ToInt64():X}");
                ImGui.Text($"Scanned Grid Address: 0x{this.scannedGridAddr.ToInt64():X}");
                ImGui.Text($"Last Scan Status: {debugLastSigSearchStatus}");
                ImGui.Text($"Visited Elements Count: {debugVisitedCount}");
                if (ImGui.TreeNode("Sample Text Values Scanned"))
                {
                    lock (debugSampleTexts)
                    {
                        foreach (var s in debugSampleTexts)
                        {
                            ImGui.Text(s);
                        }
                    }
                    ImGui.TreePop();
                }
            }
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            Mem.Close();
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState))
            {
                return;
            }

            var inGameState = Core.States.InGameStateObject;
            var areaInstance = inGameState?.CurrentAreaInstance;
            if (areaInstance != null && this.Settings.DrawWispCircle)
            {
                bool shouldDrawWisp = true;

                if (this.Settings.HideWispCircleInBackgroundOrPaused)
                {
                    if (!Core.Process.Foreground || Core.States.GameCurrentState != GameStateTypes.InGameState)
                    {
                        shouldDrawWisp = false;
                    }
                }

                if (shouldDrawWisp)
                {
                    this.DrawWispOverlay(inGameState, areaInstance);
                }
            }

            // General checks for pricing overlay / debug mode
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            if (!Core.Process.Foreground)
            {
                return;
            }

            if (!this.Settings.ShowOverlay && !this.Settings.DebugMode)
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
                    if (uiRoot != this.lastUiRoot)
                    {
                        this.scannedGridAddr = IntPtr.Zero;
                        this.lastUiRoot = uiRoot;
                        this.nextScanUtc = DateTime.MinValue;
                    }

                    // FAST PATH: fixed index chain root.children[76].children[13] = reward grid.
                    var fastGridAddr = IntPtr.Zero;
                    var fastChainValid = false;
                    if (ChildSpan(uiRoot, out var rootFirst, out var rootCount) && rootCount > 76)
                    {
                        var child76 = Ptr(rootFirst + 76 * 8);
                        if (child76 != IntPtr.Zero && ChildSpan(child76, out var child76First, out var child76Count) && child76Count > 13)
                        {
                            fastGridAddr = Ptr(child76First + 13 * 8);
                            fastChainValid = fastGridAddr != IntPtr.Zero;
                        }
                    }

                    var grid = IntPtr.Zero;
                    if (!this.Settings.ForceBfsFallback && fastChainValid)
                    {
                        var flags = Mem.Read<uint>(fastGridAddr + 0x180);
                        if (UiElementBaseFuncs.IsVisibleChecker(flags))
                        {
                            grid = fastGridAddr;
                        }
                    }
                    else
                    {
                        this.TryScanRitualWindowThrottled(uiRoot, out grid);
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
                                    // Check for runic suffix (Runeforged / Runemastered)
                                    var modsComp = ResolveComponent(item, "Mods");
                                    var runicSuffix = GetRunicSuffix(modsComp);

                                    if (!string.IsNullOrEmpty(runicSuffix))
                                    {
                                        if (!string.IsNullOrEmpty(identity.InternalNameOnly))
                                        {
                                            itemName = this.GetPrettyName(identity.InternalNameOnly, out isMapped);
                                            if (!string.IsNullOrEmpty(itemName))
                                            {
                                                priceInfo = PoeNinjaPriceFetcher.GetPrice(itemName + " " + runicSuffix);
                                            }
                                        }
                                    }

                                    if (priceInfo == null && !string.IsNullOrEmpty(identity.Art))
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
            var p = Mem.Read<IntPtr>(addr);
            if (p == IntPtr.Zero) return IntPtr.Zero;
            var u = (ulong)p;
            return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? IntPtr.Zero : p;
        }

        private static string ReadStdWString(IntPtr addr)
        {
            return Mem.ReadStdWString(addr);
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
            var details = Ptr(entity + 0x08);
            if (details == IntPtr.Zero) return IntPtr.Zero;
            var lookup = Ptr(details + 0x28);
            if (lookup == IntPtr.Zero) return IntPtr.Zero;
            var compList = Mem.Read<StdVector>(entity + 0x10);
            var compCount = ((long)compList.Last - (long)compList.First) / 8;
            if (compCount <= 0 || compCount > 256) return IntPtr.Zero;

            var bFirst = Ptr(lookup + 0x28);
            var bLast = Mem.Read<IntPtr>(lookup + 0x30);
            if (bLast == IntPtr.Zero) return IntPtr.Zero;
            var entries = ((long)bLast - (long)bFirst) / 16;
            if (bFirst == IntPtr.Zero || entries <= 0 || entries > 256) return IntPtr.Zero;

            for (long i = 0; i < entries; i++)
            {
                var e = bFirst + (int)(i * 16);
                var namePtr = Ptr(e);
                var index = Mem.Read<int>(e + 8);
                if (index < 0 || index >= compCount) continue;
                
                // Read the ASCII component name
                var bytes = Mem.ReadBytes(namePtr, 64);
                string compName = "";
                if (bytes.Length > 0)
                {
                    int z = Array.IndexOf(bytes, (byte)0);
                    compName = z >= 0 ? Encoding.ASCII.GetString(bytes, 0, z) : Encoding.ASCII.GetString(bytes);
                }
                
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

            var modsCompAddr = ResolveComponent(item, "Mods");
            if (modsCompAddr != IntPtr.Zero)
            {
                var modsComp = new Mods(modsCompAddr);
                identity.Rarity = modsComp.Rarity;
                
                var idf = Mem.Read<byte>(modsComp.Address + 0x90);
                identity.Identified = idf != 0;
            }

            var renderItemAddr = ResolveComponent(item, "RenderItem");
            if (renderItemAddr != IntPtr.Zero)
            {
                var renderComp = new RenderItem(renderItemAddr);
                identity.Art = ArtBasename(renderComp.ResourcePath);
            }

            var baseCompAddr = ResolveComponent(item, "Base");
            if (baseCompAddr != IntPtr.Zero)
            {
                var baseComp = new Base(baseCompAddr);
                if (!string.IsNullOrWhiteSpace(baseComp.BaseItemName))
                {
                    identity.Name = baseComp.BaseItemName.Trim();
                }
            }

            return identity;
        }

        private static bool ChildSpan(IntPtr el, out IntPtr first, out long n)
        {
            first = Ptr(el + 0x10);
            n = 0;
            if (first == IntPtr.Zero) return false;
            var last = Mem.Read<IntPtr>(el + 0x18);
            if (last == IntPtr.Zero) return false;
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

        private bool IsValidRewardGrid(IntPtr gridAddr)
        {
            if (gridAddr == IntPtr.Zero) return false;
            try
            {
                var flags = Mem.Read<uint>(gridAddr + 0x180);
                if (!UiElementBaseFuncs.IsVisibleChecker(flags))
                {
                    return false;
                }

                if (!ChildSpan(gridAddr, out var gf, out var gn) || gn < 1 || gn > 16)
                {
                    return false;
                }

                for (long i = 0; i < gn; i++)
                {
                    var tile = Ptr(gf + (int)(i * 8));
                    if (TileItem(tile) != IntPtr.Zero)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool TryScanRitualWindowThrottled(IntPtr gameUiRoot, out IntPtr gridAddr)
        {
            if (this.scannedGridAddr != IntPtr.Zero && this.IsValidRewardGrid(this.scannedGridAddr))
            {
                gridAddr = this.scannedGridAddr;
                return true;
            }

            this.scannedGridAddr = IntPtr.Zero;
            var now = DateTime.UtcNow;
            if (now >= this.nextScanUtc)
            {
                this.nextScanUtc = now.AddMilliseconds(750);
                this.scannedGridAddr = this.FindRitualRewardGrid(gameUiRoot);
            }

            gridAddr = this.scannedGridAddr;
            return gridAddr != IntPtr.Zero;
        }

        private IntPtr FindRitualRewardGrid(IntPtr gameUiRoot)
        {
            if (gameUiRoot == IntPtr.Zero)
            {
                debugLastSigSearchStatus = "uiRoot is Zero";
                return IntPtr.Zero;
            }

            var queue = new Queue<IntPtr>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue(gameUiRoot);
            var sigEl = IntPtr.Zero;

            lock (debugSampleTexts)
            {
                debugSampleTexts.Clear();
            }

            while (queue.Count > 0 && visited.Count < 20000)
            {
                var el = queue.Dequeue();
                if (el == IntPtr.Zero || !visited.Add(el)) continue;

                var flags = Mem.Read<uint>(el + 0x180);
                var visible = (flags & (1u << 0x0B)) != 0;
                if (!visible && el != gameUiRoot) continue; // prune invisible subtrees

                if (ChildSpan(el, out var f, out var nn))
                {
                    for (long k = 0; k < nn; k++)
                    {
                        queue.Enqueue(Ptr(f + (int)(k * 8)));
                    }
                }

                var textAddr = el + 0x390;
                var t = Mem.ReadStdWString(textAddr);
                if (!string.IsNullOrEmpty(t))
                {
                    lock (debugSampleTexts)
                    {
                        if (debugSampleTexts.Count < 15)
                        {
                            debugSampleTexts.Add(t);
                        }
                    }
                    if (t.Length >= 6 && (t.Contains("Ritual Remaining", StringComparison.OrdinalIgnoreCase) ||
                                          t.Contains("tribute to the king", StringComparison.OrdinalIgnoreCase)))
                    {
                        sigEl = el;
                        debugLastSigSearchStatus = $"Found sig at 0x{el.ToInt64():X} with text '{t}'";
                        break;
                    }
                }
            }

            debugVisitedCount = visited.Count;

            if (sigEl == IntPtr.Zero)
            {
                debugLastSigSearchStatus = $"Finished scan, visited {visited.Count} elements, sig not found";
                return IntPtr.Zero;
            }

            // Walk up from the signature element; at each ancestor look for the reward grid.
            var cur = sigEl;
            for (var up = 0; up < 8; up++)
            {
                var grid = FindRewardGrid(cur);
                if (grid != IntPtr.Zero) return grid;
                var parent = Ptr(cur + 0xB8); // Parent offset is 0xB8
                if (parent == IntPtr.Zero) break;
                cur = parent;
            }

            return IntPtr.Zero;
        }

        private static bool TryUiElementRect(IntPtr el, float winW, float winH, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0f;
            if (el == IntPtr.Zero) return false;
            
            var flags = Mem.Read<uint>(el + 0x180);
            if ((flags & (1u << 0x0B)) == 0) return false;

            var idx = Mem.Read<byte>(el + 0x18A);
            var mul = Mem.Read<float>(el + 0x130);
            var sz = Mem.Read<Vector2>(el + 0x288);

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
            var rel = Mem.Read<Vector2>(el + 0x118);
            var parent = Ptr(el + 0xB8);
            if (parent == IntPtr.Zero || depth >= 64) return (rel.X, rel.Y);

            var (ppx, ppy) = UiUnscaledPos(parent, depth + 1, winW, winH);

            var flags = Mem.Read<uint>(el + 0x180);
            if ((flags & (1u << 0x0A)) != 0)
            {
                var mod = Mem.Read<Vector2>(el + 0xF0);
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

        private static string GetRunicSuffix(IntPtr modsCompAddr)
        {
            if (modsCompAddr == IntPtr.Zero) return "";
            
            var modsComp = new Mods(modsCompAddr);
            foreach (var mod in modsComp.ImplicitMods)
            {
                if (mod.name.Contains("RuneForged", StringComparison.OrdinalIgnoreCase))
                {
                    return "Runeforged";
                }
                if (mod.name.Contains("RuneMastered", StringComparison.OrdinalIgnoreCase))
                {
                    return "Runemastered";
                }
            }
            
            return "";
        }

        private string GetPrettyName(string internalName, out bool isMapped)
        {
            isMapped = false;
            var dictionaryPath = System.IO.Path.Combine(this.DllDirectory, "item_names.json");
            
            if (this.customNamesCache == null)
            {
                this.customNamesCache = new System.Collections.Generic.Dictionary<string, string>();
                if (System.IO.File.Exists(dictionaryPath))
                {
                    try
                    {
                        var content = System.IO.File.ReadAllText(dictionaryPath);
                        var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(content);
                        if (loaded != null)
                        {
                            foreach (var kvp in loaded)
                            {
                                this.customNamesCache[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch { }
                }

                var defaults = new System.Collections.Generic.Dictionary<string, string>
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
                    { "FourGlovesStr6", "Strength Gloves" },
                    { "RitualCorruptedIdol2", "Idol of the Martyr" },
                    { "RitualCorruptedIdol1", "Idol of the Sycophant" },
                    { "RitualCorruptedIdol3", "Idol of the Pharisee" },
                    { "AzmeriSocketableCat", "Cat Idol" },
                    { "AzmeriSocketableBear", "Bear Idol" },
                    { "AzmeriSocketableBoar", "Boar Idol" },
                    { "IdoloAlira", "Idol of Alira" },
                    { "AzmeriSocketableOwlSpecial", "Idol of Eeshta" },
                    { "AzmeriSocketableCatSpecial", "Idol of Egrin" },
                    { "IdolofEramir", "Idol of Eramir" },
                    { "IdolofGreust", "Idol of Greust" },
                    { "AzmeriSocketableStagSpecial", "Idol of Maxarius" },
                    { "IdolofOak", "Idol of Oak" },
                    { "AzmeriSocketableMonkeySpecial", "Idol of Ralakesh" },
                    { "IdolofYeena", "Idol of Yeena" },
                    { "AzmeriSocketableOwl", "Owl Idol" },
                    { "AzmeriSocketableMonkey", "Primate Idol" },
                    { "AzmeriSocketableStag", "Stag Idol" },
                    { "AzmeriSocketableWolf", "Wolf Idol" },
                    { "AzmeriSocketableFox", "Fox Idol" },
                    { "AzmeriSocketableBearSpecial", "Idol of Grold" },
                    { "IdoloKraityn", "Idol of Kraityn" },
                    { "IdolofSilk", "Idol of Silk" },
                    { "AzmeriSocketableWolfSpecial", "Idol of Sirrius" },
                    { "AzmeriSocketableSnakeSpecial", "Idol of Thruldana" },
                    { "AzmeriSocketableOx", "Ox Idol" },
                    { "AzmeriSocketableRabbit", "Rabbit Idol" },
                    { "AzmeriSocketableSnake", "Snake Idol" }
                };

                bool needsSave = false;
                foreach (var kvp in defaults)
                {
                    if (!this.customNamesCache.ContainsKey(kvp.Key))
                    {
                        this.customNamesCache[kvp.Key] = kvp.Value;
                        needsSave = true;
                    }
                }

                if (needsSave || !System.IO.File.Exists(dictionaryPath))
                {
                    try
                    {
                        System.IO.File.WriteAllText(dictionaryPath, Newtonsoft.Json.JsonConvert.SerializeObject(this.customNamesCache, Newtonsoft.Json.Formatting.Indented));
                    }
                    catch { }
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
        public override void OnEnable(bool isGameOpened)
        {
            this.scannedGridAddr = IntPtr.Zero;
            this.lastUiRoot = IntPtr.Zero;
            this.nextScanUtc = DateTime.MinValue;

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
                    var grid = this.scannedGridAddr;
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
            ImGui.End();
        }

        private void DrawWispOverlay(InGameState inGameState, AreaInstance areaInstance)
        {
            var player = areaInstance.Player;
            if (player == null) return;

            var drawList = ImGui.GetBackgroundDrawList();
            var worldInstance = inGameState.CurrentWorldInstance;
            if (worldInstance == null) return;

            foreach (var kvp in areaInstance.AwakeEntities)
            {
                var ent = kvp.Value;
                if (ent == null || !ent.IsValid) continue;

                if (!string.IsNullOrEmpty(ent.Path) && ent.Path.Contains("Metadata/Monsters/LeagueRitual/RitualWispDaemon", StringComparison.OrdinalIgnoreCase))
                {
                    if (ent.TryGetComponent<Render>(out var rComp))
                    {
                        var wispWorldPos = rComp.WorldPosition;
                        var distanceGrid = player.DistanceFrom(ent);
                        float radiusGrid = this.Settings.WispCircleRadiusMeters * 10f;
                        float worldRadius = radiusGrid * (250f / 23f);

                        bool isInside = distanceGrid <= radiusGrid;
                        var colorV4 = isInside ? this.Settings.WispCircleColorInside : this.Settings.WispCircleColorOutside;
                        uint color = ImGuiHelper.Color(colorV4);

                        this.Draw3DCircle(drawList, inGameState, new Vector3(wispWorldPos.X, wispWorldPos.Y, wispWorldPos.Z), rComp.TerrainHeight, worldRadius, color, this.Settings.WispCircleThickness);
                    }
                }
            }
        }

        private void Draw3DCircle(ImDrawListPtr drawList, InGameState inGameState, Vector3 centerWorldPos, float terrainHeight, float radius, uint color, float thickness = 2f)
        {
            var worldInstance = inGameState.CurrentWorldInstance;
            if (worldInstance == null) return;

            const int numPoints = 36;
            Vector2[] points = new Vector2[numPoints];
            int validPointsCount = 0;

            for (int i = 0; i < numPoints; i++)
            {
                float angle = i * 2f * MathF.PI / numPoints;
                var pWorld = new Vector3(
                    centerWorldPos.X + radius * MathF.Cos(angle),
                    centerWorldPos.Y + radius * MathF.Sin(angle),
                    terrainHeight
                );
                var screenPos = worldInstance.WorldToScreen(new StdTuple3D<float> { X = pWorld.X, Y = pWorld.Y, Z = pWorld.Z }, pWorld.Z);
                if (screenPos != Vector2.Zero)
                {
                    points[validPointsCount++] = screenPos;
                }
            }

            if (validPointsCount > 1)
            {
                for (int i = 0; i < validPointsCount; i++)
                {
                    var p1 = points[i];
                    var p2 = points[(i + 1) % validPointsCount];
                    if (Vector2.Distance(p1, p2) < 500f)
                    {
                        drawList.AddLine(p1, p2, color, thickness);
                    }
                }
            }
        }
    }
}
