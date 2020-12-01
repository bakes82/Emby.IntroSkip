﻿using System.Collections.Generic;
using IntroSkip.Api;
using MediaBrowser.Model.Plugins;

namespace IntroSkip.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public List<IntroDto> Intros { get; set; }
        public int StartIndex = 0;
    }
}
