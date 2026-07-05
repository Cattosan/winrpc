using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WinampRPC
{
    public static class ConfigManager
    {
        private static readonly string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinampRPC");
        private static readonly string configPath = Path.Combine(configDir, "config.bin");

        static ConfigManager()
        {
            if (!Directory.Exists(configDir))
            {
                try { Directory.CreateDirectory(configDir); } catch { }
            }
        }

        public static (string clientId, string lastFmKey, bool isLightMode, string customWinampPath, bool enableVisualizer, bool autoStartVisualizer) Load()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    byte[] encryptedData = File.ReadAllBytes(configPath);
                    byte[] decryptedData = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
                    string configStr = Encoding.UTF8.GetString(decryptedData);
                    string[] parts = configStr.Split('\n');
                    
                    string clientId = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0].Trim() : "Discord App ID here";
                    string lastFmKey = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : "Last.fm API key here";
                    bool isLightMode = parts.Length > 2 && parts[2].Trim() == "1";
                    string customWinampPath = parts.Length > 3 ? parts[3].Trim() : "";
                    bool enableVisualizer = parts.Length > 4 ? parts[4].Trim() == "1" : true;
                    bool autoStartVisualizer = parts.Length > 5 ? parts[5].Trim() == "1" : true;
                    
                    return (clientId, lastFmKey, isLightMode, customWinampPath, enableVisualizer, autoStartVisualizer);
                }
                
                // Fallback to old config.txt if config.bin doesn't exist
                string oldConfigPath = Path.Combine(AppContext.BaseDirectory, "config.txt");
                if (File.Exists(oldConfigPath))
                {
                    string[] lines = File.ReadAllLines(oldConfigPath);
                    string clientId = lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]) ? lines[0].Trim() : "Discord App ID here";
                    string lastFmKey = lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]) ? lines[1].Trim() : "Last.fm API key here";
                    
                    // Save securely and delete old file
                    Save(clientId, lastFmKey, false, "", true, true);
                    File.Delete(oldConfigPath);
                    
                    return (clientId, lastFmKey, false, "", true, true);
                }
            }
            catch { }
            return ("Discord App ID here", "Last.fm API key here", false, "", true, true);
        }

        public static void Save(string clientId, string lastFmKey, bool isLightMode, string customWinampPath, bool enableVisualizer, bool autoStartVisualizer)
        {
            try
            {
                string configStr = $"{clientId}\n{lastFmKey}\n{(isLightMode ? "1" : "0")}\n{customWinampPath}\n{(enableVisualizer ? "1" : "0")}\n{(autoStartVisualizer ? "1" : "0")}";
                byte[] encryptedData = ProtectedData.Protect(Encoding.UTF8.GetBytes(configStr), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(configPath, encryptedData);
            }
            catch { }
        }
    }
}
