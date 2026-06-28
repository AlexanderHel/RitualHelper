// <copyright file="RitualHelperSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RitualHelper
{
    using GameHelper.Plugin;
    using System.Numerics;

    /// <summary>
    /// <see cref="RitualHelper"/> plugin settings class.
    /// </summary>
    public sealed class RitualHelperSettings : IPSettings
    {
        /// <summary>
        ///     Gets or sets a value indicating whether the overlay window is shown.
        /// </summary>
        public bool ShowOverlay = true;


        /// <summary>
        ///     Gets or sets a value indicating whether to enable debug mode (shows all inventories).
        /// </summary>
        public bool DebugMode = false;

        /// <summary>
        ///     Gets or sets a value indicating whether to force the signature BFS fallback search.
        /// </summary>
        public bool ForceBfsFallback = false;

        /// <summary>
        ///     Gets or sets a value indicating whether to hide the wisp range circle when the game is in the background or paused.
        /// </summary>
        public bool HideWispCircleInBackgroundOrPaused = false;

        /// <summary>
        ///     Gets or sets a value indicating whether to draw a circle around the Ritual Wisp.
        /// </summary>
        public bool DrawWispCircle = true;

        /// <summary>
        ///     Gets or sets the color of the circle when the player is inside the wisp range.
        /// </summary>
        public Vector4 WispCircleColorInside = new(0f, 1f, 0f, 1f);

        /// <summary>
        ///     Gets or sets the color of the circle when the player is outside the wisp range.
        /// </summary>
        public Vector4 WispCircleColorOutside = new(1f, 0.9f, 0f, 1f);

        /// <summary>
        ///     Gets or sets the radius of the wisp circle in meters.
        /// </summary>
        public float WispCircleRadiusMeters = 3.0f;

        /// <summary>
        ///     Gets or sets the thickness of the wisp circle.
        /// </summary>
        public float WispCircleThickness = 2.0f;
    }
}
