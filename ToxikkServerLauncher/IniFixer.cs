using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace ToxikkServerLauncher
{
  class IniFixer
  {
    private static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string configFolder;

    public IniFixer(string configFolder)
    {
      this.configFolder = configFolder;
    }

    #region GetTimestamp()
    /// <summary>
    /// This method creates a timestamp for a Default*.ini file as it is expected by the bugged UDK.exe code.
    /// If Windows' automatic DST adjustment is disabled (not-default), the timestamps inside the UDK .ini are correct UTC epoch timestamps.
    /// With DST adjustment enabled, the timestamp is 1 hours off (early or late) depending on today's DST setting and if the default file's date is in DST
    /// </summary>
    public long GetTimestamp(string file)
    {
      var defaultFileTimeUtc = File.GetLastWriteTimeUtc(file);

      int offset = 0;
      if (GetWindowsAutomaticDaylightSavingAdjustment())
      {
        if (TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.UtcNow))
        {
          if (!TimeZoneInfo.Local.IsDaylightSavingTime(defaultFileTimeUtc))
            offset = +3600;
        }
        else
        {
          if (TimeZoneInfo.Local.IsDaylightSavingTime(defaultFileTimeUtc))
            offset = -3600;
        }
      }

      return (defaultFileTimeUtc - epoch).Ticks / TimeSpan.TicksPerSecond + offset;
    }

    private bool GetWindowsAutomaticDaylightSavingAdjustment()
    {
      try
      {
        var result = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "DynamicDaylightTimeDisabled", 1);
        return !Convert.ToBoolean(result); //0 - Checked/enabled,  1 - Unchecked/disabled
      }
      catch
      {
        return false;
      }
    }

    #endregion

    #region FixTimestamps()
    /// <summary>
    /// Sets the timestamps inside a UDK*.ini file's [IniVersion] section to match the file timestamps of the corresponding Default*.ini files and their base files.
    /// Optionally this can be limited to files where the timestamp is off by exactly one hour to fix the timestamps for the current daylight saving time.
    /// </summary>
    /// <param name="daylightSavingCorrectionOnly">Only fix timestamps when they are off by exactly 1 hour due to daylight saving changes</param>
    public void FixTimestamps(bool daylightSavingCorrectionOnly)
    {
      foreach (var udkIniFilePath in Directory.GetFiles(configFolder, "UDK*.ini"))
      {
        string defaultIniFilePath = Path.Combine(configFolder, "Default" + Path.GetFileName(udkIniFilePath).Substring(3));
        if (!File.Exists(defaultIniFilePath))
          continue;

        var ini = new IniFile(udkIniFilePath);
        var sec = ini.GetSection("IniVersion");
        if (sec == null)
          continue;

        bool saveFile = true;
        List<long> timestamps = new List<long>();
        CollectDefaultIniTimestamps(defaultIniFilePath, timestamps);
        for (int i = 0; i < timestamps.Count; i++)
        {
          var newTimestamp = timestamps[i];
          if (daylightSavingCorrectionOnly)
          {
            var oldTimestamp = (long) sec.GetDecimal(i.ToString());
            if (oldTimestamp != 0 && oldTimestamp != newTimestamp && Math.Abs(oldTimestamp - newTimestamp) != 3600)
            {
              saveFile = false;
              break;
            }
          }
          sec.Set(i.ToString(), newTimestamp.ToString());
        }

        if (saveFile)
          ini.Save();
      }
    }

    /// <summary>
    /// Recursively collect the timestamps from a file's [IniVersion] section and all the files included through [Configuration].BasedOn
    /// The most basic file can be found at index 0.
    /// </summary>
    private void CollectDefaultIniTimestamps(string defaultIniFilePath, List<long> timestamps)
    {
      IniFile defaultIni = new IniFile(defaultIniFilePath);
      var conf = defaultIni.GetSection("Configuration");
      var baseIni = conf?.GetString("BasedOn");
      if (!string.IsNullOrEmpty(baseIni))
      {
        var baseFile = Path.Combine(configFolder, "..", baseIni);
        if (File.Exists(baseFile))
          CollectDefaultIniTimestamps(baseFile, timestamps);
      }
      timestamps.Add(GetTimestamp(defaultIniFilePath));
    }
    #endregion
  }
}
