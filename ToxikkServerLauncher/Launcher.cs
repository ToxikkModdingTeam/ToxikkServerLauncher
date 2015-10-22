using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ToxikkServerLauncher
{
  class Launcher
  {
    private const string ServerSectionPrefix = "DedicatedServer";
    private string launcherFolder;
    private string toxikkFolder;
    private string configFolder;
    private string toxikkExe;
    private IniFile ini;

    /// <summary>
    /// Maps logical server setting names to real setting names (either ini-file\section\section specifier or a command line option name) as found in the [SimpleNames] section
    /// </summary>
    private readonly Dictionary<string,string> keyMapping = new Dictionary<string, string>();

    #region Run()
    public void Run(string[] args)
    {
      var serverIds = ParseCommandLine(args);
      if (serverIds == null)
        return;

      ReadServerConfig();

      if (serverIds.Count == 0)
      {
        ListConfigurations();
        Console.Write("Start server(s): ");
        serverIds = (Console.ReadLine() ?? "").Split(' ').ToList();
      }

      foreach (var id in serverIds)
        RunServerConfiguration(id);
    }
    #endregion

    #region ParseCommandLine()
    private List<string> ParseCommandLine(string[] args)
    {
      List<string> serverIds = new List<string>();

      foreach (var arg in args)
      {
        if (arg[0] == '/' || arg[0] == '-')
        {
          int idx = arg.IndexOf('=');
          string key = idx < 0 ? arg : arg.Substring(1, idx-1);
          string val = idx < 0 ? "" : arg.Substring(idx + 1);
          switch (key.ToLower())
          {
            case "toxikkdir":
              toxikkFolder = val;
              break;
          }
        }
        else
          serverIds.Add(arg);
      }

      launcherFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\";
      if (toxikkFolder == null)
        toxikkFolder = launcherFolder + @"..";
      toxikkExe = toxikkFolder + @"\Binaries\Win32\TOXIKK.exe";
      configFolder = toxikkFolder + @"\UDKGame\Config\";

      if (!File.Exists(toxikkExe))
      {
        Console.Error.WriteLine(@"Either copy this program to SteamApps\Common\TOXIKK\TOXIKKServers or use /toxikkdir=...");
        return null;
      }
      return serverIds;
    }

    #endregion

    #region ReadServerConfig()
    private void ReadServerConfig()
    {
      var configFile = launcherFolder + "\\ServerConfig.ini";
      ini = new IniFile(configFile);

      // import old server config file format if necessary
      if (!ini.Sections.Any(s => s.Name.StartsWith(ServerSectionPrefix)))
      {
        Console.WriteLine("No [" + ServerSectionPrefix + "...] sections found in ServerConfig.ini. Importing settings from ServerConfigList.ini ...");
        ConvertLegacyServerConfigListIni();
        ini = new IniFile(configFile);
      }

      // build dictionary for "simple name" translation
      var section = ini.GetSection("SimpleNames");
      foreach (var key in section.Keys)
        keyMapping[key] = section.GetString(key) ?? "";
    }
    #endregion

    #region ConvertLegacyServerConfigListIni()
    private void ConvertLegacyServerConfigListIni()
    {
      var oldConfigFile = toxikkFolder + @"\TOXIKKServers\TOXIKKServerLauncher\ServerConfigList.ini";
      if (!File.Exists(oldConfigFile))
        return;

      bool inList = false;
      int serverNumber = 0;

      foreach (var rawLine in File.ReadAllLines(oldConfigFile))
      {
        var line = rawLine.Trim();

        if (line == "" || line.StartsWith(";"))
          continue;

        if (!inList)
        {
          if (line == "BeginList")
            inList = true;
          continue;
        }

        // extract the 14 columns
        var fields = line.Split(' ', '\t').Where(f => f != "").Select(f => f.Trim('"')).ToList();
        for (int i = 0; i < 14; i++)
          fields.Add("");

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.Append("[").Append(ServerSectionPrefix).Append(++serverNumber).AppendLine("]");
        sb.AppendLine($"Map={fields[0]}");
        sb.AppendLine($"GameMode={TranslateGameMode(fields[1])}");
        sb.AppendLine($"GoalScore={fields[2]}");
        sb.AppendLine($"TimeLimit={fields[3]}");
        sb.AppendLine($"MinPlayers={fields[4]}");
        sb.AppendLine($"MaxPlayers={fields[5]}");
        sb.AppendLine($"GamePassword={fields[6]}");
        sb.AppendLine($"AdminPassword={fields[7]}");
        sb.AppendLine($"ServerName={fields[8].Replace('_', ' ')}");
        sb.AppendLine($"GamePort={fields[9]}");
        sb.AppendLine($"QueryPort={fields[10]}");
        sb.AppendLine($"MinSkillClass={fields[11]}");
        sb.AppendLine($"MaxSkillClass={fields[12]}");
        sb.AppendLine($"Mutators={fields[13]}");

        File.AppendAllText(launcherFolder + "ServerConfig.ini", sb.ToString());
      }
    }

    private string TranslateGameMode(string mode)
    {
      if (mode == "BL") return "Cruzade.CRZBloodlust";
      if (mode == "SA") return "Cruzade.CRZTeamGame";
      if (mode == "CC") return "Cruzade.CRZCellCapture";
      return mode;
    }
    #endregion

    #region ListConfigurations()
    private void ListConfigurations()
    {
      Console.WriteLine("Available server configurations:");
      foreach (var section in ini.Sections)
      {
        if (section.Name.StartsWith(ServerSectionPrefix))
          Console.WriteLine($"{section.Name.Substring(ServerSectionPrefix.Length),3}: {section.GetString("ServerName")}");
      }
    }
    #endregion

    #region RunServerConfiguration()
    private void RunServerConfiguration(string serverId)
    {
      if (serverId.Trim() == "")
        return;

      var sectionName = ServerSectionPrefix + serverId;
      var section = ini.GetSection(sectionName);
      if (section == null)
      {
        Console.Error.WriteLine("No configuration section for " + sectionName);
        return;
      }

      Console.WriteLine("Starting " + (section.GetString("ServerName") ?? sectionName));
      var options = GenerateConfig(section);
      LaunchServer(sectionName, options);
    }

    #endregion

    #region GenerateConfig()
    private string GenerateConfig(IniFile.Section section)
    {
      var configDir = configFolder + section.Name;
      Directory.CreateDirectory(configDir);
      foreach (var file in Directory.GetFiles(configFolder, "Default*.ini"))
        File.Copy(file, configDir + "\\" + Path.GetFileName(file), true);

      var destIniCache = new Dictionary<string, IniFile>();

      var options = new StringBuilder();

      ProcessConfigSection(section.Name, section, destIniCache, options);

      foreach (var destIni in destIniCache.Values)
        destIni.Save();

      return options.ToString();
    }
    #endregion

    #region ProcessConfigSection()
    private void ProcessConfigSection(string sectionName, IniFile.Section section, Dictionary<string, IniFile> destIniCache, StringBuilder options)
    {
      foreach (var unmappedKey in section.Keys)
      {
        var value = section.GetString(unmappedKey);
        string mappedKey;
        if (!keyMapping.TryGetValue(unmappedKey, out mappedKey))
          mappedKey = unmappedKey;

        var configMapping = mappedKey.Split('\\');
        if (configMapping.Length == 3)
        {
          IniFile destIni;
          string destIniPath = configFolder + sectionName + "\\" + configMapping[0];
          if (!destIniCache.TryGetValue(destIniPath, out destIni))
          {
            destIni = new IniFile(destIniPath);
            destIniCache.Add(destIniPath, destIni);
          }
          var destSec = destIni.GetSection(configMapping[1], true);
          destSec.Set(configMapping[2], value);
        }
        else if (mappedKey.ToLower() == "map")
          options.Insert(0, value);
        else if (mappedKey.ToLower() == "@import")
          ProcessConfigSection(sectionName, ini.GetSection(value), destIniCache, options);
        else if (mappedKey.ToLower() == "@copyfiles")
        {
          foreach (var fileInfo in value.Split(','))
          {
            var names=fileInfo.Split('=',':','\\','/');
            if (names.Length == 2 && names[0] != "" && names[1] != "")
              File.Copy(configFolder + names[0], configFolder + sectionName + "\\" + names[1], true);
          }
        }
        else
          options.Append("?").Append(mappedKey).Append("=").Append(value.Replace(' ', '_')); // WebUtility.UrlEncode(value)
      }
    }

    #endregion

    #region LaunchServer()
    private void LaunchServer(string sectionName, string options)
    {
      System.Diagnostics.Process.Start(toxikkExe, "server " + options + "?dedicated=true?steamsockets -nohomedir -unattended -CONFIGSUBDIR=" + sectionName);
    }
    #endregion

  }
}
