# RitualHelper (AutoRitualPricer)

RitualHelper is a lightweight, high-performance plugin for [GameHelper](https://github.com/Queuete/GameHelper) that automatically evaluates and displays the prices of items inside the Path of Exile 2 Ritual Tribute Shop using real-time data from **poe.ninja**.

## Features

- ⚡ **Lightning Fast UI Scanning**: Utilizes a highly optimized, signature-based BFS UI tree walk to locate the Ritual Tribute Shop grid. This bypasses slow memory reflection and reads native components directly from the game's memory, ensuring zero impact on your framerate.
- 💸 **Live Pricing via Poe.Ninja**: Fetches live market prices in the background for currencies, unique armours, accessories, charms, and more.
- 🎨 **2D-Art Based Pricing**: Identifies items precisely by reading their internal 2D Art resource paths (`RenderItem` components). This ensures that items with the same base name but different unique identities are priced flawlessly without needing manual mapping.
- 💊 **Clean Overlay UI**: Draws beautiful, centered pill-chip overlays at the bottom edge of each item in the Ritual window, dynamically displaying prices in **Chaos (c)** or **Divine (d)** orbs.
- 📋 **Fallback Auto-Mapping (Ctrl+C)**: For unidentified items or items without a known 2D art mapping, hover over the item and press `Ctrl+C`. The plugin will instantly read the clipboard, map the internal name to the English unique/currency name, and update the overlay.

## Installation

1. Ensure you have [GameHelper](https://github.com/Queuete/GameHelper) installed.
2. Download or clone this repository into the `Plugins` folder of your GameHelper installation:
   `GameHelper/Plugins/RitualHelper/`
3. The GameHelper framework must be updated to include `[assembly: InternalsVisibleTo("RitualHelper")]` in its `Core.cs` to allow raw memory reading.
4. Compile the plugin using `dotnet build` or allow GameHelper to auto-compile it on startup.
5. Launch the game and GameHelper. The plugin will silently download the latest poe.ninja prices.

## How to Use

1. Open the Ritual Tribute Shop (Favours window) in Path of Exile 2.
2. The plugin will instantly scan the UI and draw price chips over the valuable items.
3. Use the GameHelper overlay (`F12`) to toggle settings like Debug Mode (to view native UI memory addresses) or to disable the overlay.

## Requirements

- Path of Exile 2
- GameHelper v2.4.0+
- .NET 10.0 SDK (for compilation)
