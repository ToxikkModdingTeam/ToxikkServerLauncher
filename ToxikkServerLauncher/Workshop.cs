using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using ICSharpCode.SharpZipLib.Zip;

namespace ToxikkServerLauncher
{
  /// <summary>
  /// This class handles updates of 
  /// - TOXIKK game files through steamcmd
  /// - Steam Workshop items through steamcmd
  /// - non-workshop items through .zip file URLs
  /// </summary>
  public class Workshop
  {
    #region class ItemStatus
    private class ItemStatus
    {
      public readonly string FolderName;
      public readonly long WorkshopId;
      public readonly string ZipUrl;
      public readonly bool RequireDownload;

      public ItemStatus(string iniValue, string workshopFolder, bool forceDownload)
      {
        int i = iniValue.IndexOf(";");
        if (i >= 0)
          iniValue = iniValue.Substring(0, i);
        iniValue = iniValue.Trim();
        if (long.TryParse(iniValue, out this.WorkshopId))
          this.FolderName = WorkshopId.ToString();
        else if ((iniValue.StartsWith("http://") || iniValue.StartsWith("https://")) && iniValue.EndsWith(".zip"))
        {
          this.ZipUrl = iniValue;
          this.FolderName = Path.GetFileNameWithoutExtension(new Uri(iniValue).AbsolutePath);
        }
        else
          this.FolderName = iniValue;

        var dir = Path.Combine(workshopFolder, this.FolderName);
        bool isDownloadable = WorkshopId != 0 || !string.IsNullOrEmpty(ZipUrl);
        this.RequireDownload = isDownloadable && (forceDownload || !Directory.Exists(dir) || Directory.GetDirectories(dir).Length == 0);
      }
    }
    #endregion

    private readonly Launcher launcher;

    public Workshop(Launcher launcher)
    {
      this.launcher = launcher;
    }

    #region UpdateWorkshop()
    public void UpdateWorkshop(bool forceUpdate)
    {
      if (launcher.CleanWorkshop)
        CleanWorkshopFolder();

      var itemStatus = CheckItemStatus(forceUpdate);
      DownloadSteamWorkshopItems(itemStatus);
      DownloadZipItems(itemStatus);

      if (launcher.SyncWorkshop)
      {
        Utils.WriteLine("Copying workshop item contents to TOXIKK and HTTP redirect folders...");
        if (launcher.ServerProcessesRunning)
          Utils.WriteLine("^EWARNING^7: TOXIKK.exe is already running, updates may fail.");
        CopyWorkshopContent(itemStatus);
      }
    }
    #endregion

    #region CleanWorkshopFolder()
    private void CleanWorkshopFolder()
    {
      // delete the manifest file to make sure we really download
      Utils.WriteLine("Cleaning " + launcher.WorkshopFolder);
      File.Delete(Path.Combine(launcher.WorkshopFolder, @"..\..\appworkshop_324810.acf"));

      // delete all numeric folders (which can be reacquired from steam) but keep alphanumeric folders (with developer content)
      foreach (var itemFolder in Directory.GetDirectories(launcher.WorkshopFolder))
      {
        long dummy;
        if (long.TryParse(Path.GetFileName(itemFolder), out dummy))
        {
          try { Directory.Delete(itemFolder, true); }
          catch (IOException ex)
          {
            Utils.WriteLine("^CERROR:^7 couldn't delete " + itemFolder + ": " + ex.Message);
          }
        }
        else
        {
          Utils.WriteLine("^FINFO:^7 keeping non-steam folder " + itemFolder);
        }
      }
    }

    #endregion

    #region CheckItemStatus()
    private List<ItemStatus> CheckItemStatus(bool forceUpdate)
    {
      var itemList = new List<ItemStatus>();
      foreach (var sec in new[] { launcher.MainIni.GetSection("SteamWorkshop"), launcher.MainIni.GetSection("SteamWorkshop:" + launcher.MachineName) })
      {
        if (sec == null)
          continue;

        var items = sec.GetAll("Item");
        foreach (var item in items)
        {
          if (item.Operator == ":=" || item.Operator == "!=")
          {
            itemList.Clear();
            continue;
          }

          var newStatus = new ItemStatus(item.Value, launcher.WorkshopFolder, forceUpdate);
          var oldStatus = itemList.FirstOrDefault(e => e.FolderName == newStatus.FolderName);
          if (oldStatus != null)
          {
            if (item.Operator == "-=")
              itemList.Remove(oldStatus);
            continue;
          }

          itemList.Add(newStatus);
        }
      }
      return itemList;
    }
    #endregion

    #region DownloadWorkshopItems()
    private void DownloadSteamWorkshopItems(List<ItemStatus> items)
    {
      if (!launcher.UpdateToxikk && items.Count(e => e.RequireDownload && e.WorkshopId != 0) == 0)
        return;

      if (launcher.SteamcmdExe == null)
      {
        Utils.WriteLine("^EWARNING:^7 Steamcmd not configured, skipping workshop updates.");
        return;
      }

      var sec1 = launcher.MainIni.GetSection("SteamWorkshop:" + launcher.MachineName);
      var sec2 = launcher.MainIni.GetSection("SteamWorkshop");

      var user = sec1?.GetString("User") ?? sec2.GetString("User");
      var pass = sec1?.GetString("Password") ?? sec2.GetString("Password");
      if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
      {
        Utils.WriteLine("^EWARNING:^7 User/Password not configured in [SteamWorkshop], skipping workshop updates.");
        return;
      }

      var sb = new StringBuilder();
      sb.Append("+login ").Append(user).Append(" ").Append(pass);
      if (launcher.UpdateToxikk)
        sb.Append(" +force_install_dir \"").Append(launcher.ToxikkFolder).Append("\" +app_update 324810");
      foreach (var item in items)
      {
        if (item.RequireDownload && item.WorkshopId != 0)
          sb.Append(" +workshop_download_item 324810 ").Append(item.WorkshopId);
      }
      sb.Append(" +quit");

      Utils.WriteLine("Updating TOXIKK and Steam Workshop Items...\n");
      var psi = new ProcessStartInfo(launcher.SteamcmdExe, sb.ToString());
      psi.UseShellExecute = false;
      var proc = Process.Start(psi);
      proc?.WaitForExit();
      Utils.WriteLine("\nSteam update complete.\n");
    }
    #endregion

    #region DownloadZipItems()
    private void DownloadZipItems(List<ItemStatus> items)
    {
      CountdownEvent countdown = new CountdownEvent(1);
      foreach (var item in items)
      {
        var cli = new WebClient();
        cli.DownloadFileCompleted += OnDownloadZipItemCompleted;


        if (!item.RequireDownload || string.IsNullOrEmpty(item.ZipUrl))
          continue;

        Utils.WriteLine("Downloading " + item.ZipUrl + " ...");
        countdown.AddCount(1);
        var file = Path.Combine(launcher.WorkshopFolder, item.FolderName + ".zip");
        File.Delete(file);
        cli.DownloadFileAsync(new Uri(item.ZipUrl), file, new Tuple<string, ItemStatus, CountdownEvent>(file, item, countdown));
      }
      countdown.Signal();
      countdown.Wait();

    }
    #endregion

    #region OnDownloadZipItemCompleted()
    private void OnDownloadZipItemCompleted(object sender, AsyncCompletedEventArgs e)
    {
      var context = (Tuple<string, ItemStatus, CountdownEvent>) e.UserState;
      var file = context.Item1;
      var item = context.Item2;
      var countdown = context.Item3;

      if (e.Error != null)
        Utils.WriteLine("^CERROR:^7 Failed to download " + item.ZipUrl);
      else
      {
        FastZip zip = new FastZip();
        var dir = Path.Combine(launcher.WorkshopFolder, item.FolderName);
        try
        {
          zip.ExtractZip(file, dir, FastZip.Overwrite.Always, null, null, null, true);

          // if extracting the zip created paths like 324810\MyItem\MyItem\Content, move the subfolders one level up
          var dirItems = Directory.GetFileSystemEntries(dir);
          if (dirItems.Length == 1 && Directory.Exists(Path.Combine(dir, item.FolderName)))
          {
            foreach (var subDir in Directory.GetFileSystemEntries(dirItems[0]))
            {
              if (Directory.Exists(subDir))
                Directory.Move(subDir, Path.Combine(dir, Path.GetFileName(subDir) ?? ""));
              else
                File.Move(subDir, Path.Combine(dir, Path.GetFileName(subDir) ?? ""));
            }
            Directory.Delete(dirItems[0]);
          }
        }
        catch (Exception ex)
        {
          Utils.WriteLine("^CERROR:^7 Failed to extract " + file + ": " + ex.Message);
        }
      }
      countdown.Signal();
      File.Delete(file);
      ((WebClient)sender).Dispose();
    }
    #endregion

    #region CopyWorkshopContent()
    private void CopyWorkshopContent(List<ItemStatus> items)
    {
      if (!Directory.Exists(launcher.WorkshopFolder))
        return;

      if (launcher.HttpFolder == null)
        Utils.WriteLine("^EWARNING:^7 no HTTP redirect folder configured. Clients won't be able to auto-download workshop items.");

      var toxikkWorkshopDir = Path.Combine(launcher.ToxikkFolder, @"UDKGame\Workshop");
      try
      {
        // delete existing files so only content of the workshop items listed in the .ini survives
        if (Directory.Exists(toxikkWorkshopDir))
          Directory.Delete(toxikkWorkshopDir, true);
      }
      catch (IOException ex)
      {
        Utils.WriteLine($"^CERROR:^7 Failed to delete {toxikkWorkshopDir}: {ex.Message}");
      }

      foreach (var item in items)
      {
        var itemPath = Path.Combine(launcher.WorkshopFolder, item.FolderName);
        try
        {
          if (Directory.Exists(itemPath))
            launcher.CopyFolder(itemPath, toxikkWorkshopDir);
          else
            Utils.WriteLine($"^EWARNING:^7 Workshop item folder not found: {itemPath}");
        }
        catch (IOException ex)
        {
          Utils.WriteLine($"^CERROR:^7 Failed to copy workshop item {itemPath}: {ex.Message}");
        }
      }
    }
    #endregion
  }
}
