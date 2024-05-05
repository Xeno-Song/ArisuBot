using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ArisuCore.Config
{
    internal class ConfigManager
    {
        public static DiscordConfig? LoadDiscordConfig(string filepath = "../../../../../secret/discord_config.json")
        {
            return JsonSerializer.Deserialize<DiscordConfig>(File.ReadAllText(filepath));
        }
    }
}
