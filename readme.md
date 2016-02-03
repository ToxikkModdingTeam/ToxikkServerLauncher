ToxikkServerLaucher
===

This little tool helps you manage a whole set of different server configurations without the typical copy&paste, version update, ... issues.
Main features:
- update TOXIKK and workshop items through steamcmd.exe
- automatically copies downloaded workshop items into TOXIKK and HTTP redirect folders
- single centralized configuration file used to dynamically generate config folders and files for individual servers.
- builds the command line with server specific options needed to launch TOXIKK.exe as a dedicated server.
- allows you to use friendly settings names instead of the real INI or command line option names.

The @Import=\<source\> command allows you to re-use common configuration parts in multiple server configurations. It can import other sections from the current file,
or sections from other files, even in subdirectories. When using subdirectories, it also sets the source directory for files used with @CopyFiles.   
The @CopyFiles=\<from:to,...\> command allows you to copy+rename special configuration files for specific servers (e.g. DefaultMapList.ini with only BL/SA maps).

For port number values a macro can be used to automatically generate the number from the X in DedicatedServerX:
The value "@port,10000,2" is calculated as 10000 + (X-1) * 2. Your DedicatedServer1 will use port 10000, DedicatedServer2 will use 10002, ...

Setup
-----
See FIRST STEPS inside ServerConfig.ini and edit the necessary settings. The file will be automatically renamed to MyServerConfig.
The first time you start the .exe it will import your old server settings from TOXIKKServerLauncher\\ServerConfigList.ini into ServerConfig.ini. 
(You can force a re-import by removing all [DedicatedServer...] sections from ServerConfig.ini.)

Usage
-----
When you start the .exe without any command line parameters, you'll get a list of available server configurations. 
Enter the configuration ID (or multiple separated by spaces) to start server instances.  
You can also specify the configuration IDs on the command line to start the server(s) without user input.  
To auto-download updates from Steam, all TOXIKK.exe processes must be terminated first to unlock files.


What it does
------------
The launcher copies SteamApps\\Common\\TOXIKK\\UDKGame\\Config\\Default*.ini to a DedicatedServer... subdirectory, overwriting any existing files.
It also copies all UDK*.ini files which don't have a corresponding Default*.ini. (Such files are needed for custom mutators to solve a race condition 
between accessing the UDK* file and generating it from a Default* file.)
Then it applies the INI changes defined in your ServerConfig.ini [DedicatedServer...] sections to the files in the subdirectory.
All settings in ServerConfig.ini which don't refer to an INI file are used as the command line options for toxikk.exe.

ServerConfig.ini
----------------
There's lots of comments inside the file to help you build your own config.

The \[ServerLauncher\] section contains some settings for the launcher itself, mostly directory names you need to adjust.

The \[SteamWorkshop\] section contains your login information and the workshop item numbers you want to download/update.

The \[SimpleNames\] section can be used to define friendlier names to be used inside your other sections instead of the full INI path or command line option name.

The \[DedicatedServer...\] sections contain your actual server configurations, or more precicely the settings that need to be changed from the standard settings.  

Other sections have no direct meaning, but can be used with @Import=\<section-name\> to re-use common settings, e.g. for Bloodlust or Cell Capture servers.

[Example ServerConfig.ini](ToxikkServerLauncher/ServerConfig.ini)


Compiling from source
---------------------
I used VS2015 and .NET 4.0.  
