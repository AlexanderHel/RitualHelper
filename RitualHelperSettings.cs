// <copyright file="RitualHelperSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RitualHelper
{
    using GameHelper.Plugin;

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
    }
}
