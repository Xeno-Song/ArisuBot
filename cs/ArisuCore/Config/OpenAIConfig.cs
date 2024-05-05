using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ArisuCore.Config
{
    internal class OpenAIConfig
    {
        public string? ApiKey { get; set; }

        public static OpenAIConfig? LoadConfig(string filepath)
        {
            return JsonSerializer.Deserialize<OpenAIConfig>(File.ReadAllText(filepath));
        }
    }
}
