ToxikkServerLaucher
===

This little tool helps you manage a whole set of different server configurations without the typical copy&paste, version update, ... issues.
It dynamically generates configuration file folders from template files and then applies the changes for individual servers.
It also builds the command line with server specific options needed to launch toxikk.exe.
And it allows you to use frendly settings names instead of the real INI or command line option names.

The @Import=<section-name> command allows you to re-use common configuration parts in multiple server configurations.  
The @CopyFiles=\<from:to,...\> command allows you to copy+rename special configuration files for specific servers (e.g. DefaultMapList.ini with only BL/SA maps).

Setup
-----
Put ToxikkServerLauncher.exe and ServerConfig.ini in your SteamApps\\Common\\TOXIKK\\TOXIKKServers directory.  
The first time you start the .exe it will import your old server settings from TOXIKKServerLauncher\\ServerConfigList.ini into ServerConfig.ini. 
(You can force a re-import by removing all [DedicatedServer...] sections from ServerConfig.ini.)

Usage
-----
When you start the .exe without any command line parameters, you'll get a list of available server configurations. 
Enter the configuration ID (or multiple separated by spaces) to start it/them.
You can also specify the configuration IDs on the command line to start the server(s) without user input.

What it does
------------
The launcher copies SteamApps\\Common\\TOXIKK\\UDKGame\\Config\\Default*.ini to a DedicatedServer... subdirectory, overwriting any existing files.
It also copies all UDK*.ini files which don't have a corresponding Default*.ini. (Such files are needed for custom mutators to solve a race condition 
between accessing the UDK* file and generating it from a Default* file.)
It then applies the INI changes defined in your ServerConfig.ini [DedicatedServer...] sections to the files in the subdirectory.
All settings in ServerConfig.ini which don't refer to an INI file are used to build the command line options for toxikk.exe.

ServerConfig.ini
----------------
There's lots of commentry inside the file to help you build your own config.

The \[SimpleNames\] section can be used to define friendlier names to be used inside your other sections instead of the full INI path or command line option name.

The \[DedicatedServer...\] sections contain your actual server configurations, or more precicely the settings that need to be changed from the standard settings.  

Other sections have no direct meaning, but can be used with @Import=\<section-name\> to re-use common settings, e.g. for Bloodlust or Cell Capture servers.

[Example ServerConfig.ini](ToxikkServerLauncher/ServerConfig.ini)


Compiling from source
---------------------
I used VS2015 and .NET 4.0.  
You can use the command line switch /toxikkdir=\<path-to-steamapps-common-toxikk\> in your project's debug settings to tell the program where to find toxikk.