using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ArisuCore.Config
{
    internal class DiscordConfig
    {
        public string? Token { get; set; }

        public static DiscordConfig? LoadConfig(string filepath)
        {
            return JsonSerializer.Deserialize<DiscordConfig>(File.ReadAllText(filepath));
        }
    }
}
