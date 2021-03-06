﻿using System;

using Oxide.Core.Plugins;

namespace Oxide.Game.SpaceEngineers
{
    /// <summary>
    /// Responsible for loading Space Engineers core plugins
    /// </summary>
    public class SpaceEngineersPluginLoader : PluginLoader
    {
        public override Type[] CorePlugins => new[] { typeof(SpaceEngineersCore) };
    }
}
