ToxikkServerLaucher
===

This little tool helps you manage a whole set of different server configurations without the typical copy&paste, version update, ... issues.

Main features:
- update TOXIKK and workshop items through steamcmd.exe
- automatically copies downloaded workshop items into TOXIKK and HTTP redirect folders
- single centralized configuration file used to dynamically generate config folders and files for individual servers and machines.
- builds the command line with server specific options needed to launch TOXIKK.exe as a dedicated server.
- friendly settings names instead of the real INI or command line option names.
- allows use of =, += and -= to set/add/remove a value for/to/from an .ini setting array, option URL parameter or the TOXIKK command line.
- machine specific setting overrides by using sections like \[\<section\>:\<machine\>], so the same config file can be used on multiple machines.
- centralized definition of values which can be reused in ini/option values with \@varname\@ placeholders.

Special names recognized on the left hand side of an assignment:
- @Import=\<source\> command allows you to re-use common configuration parts in multiple server configurations. It can import other sections from the current file,
or sections from other files, even in subdirectories. When using subdirectories, it also sets the source directory for files used with @CopyFiles.   
- @CopyFiles=\<from:to,...\> command allows you to copy+rename special configuration files for specific servers (e.g. DefaultMapList.ini with only BL/SA maps).   
- @CmdLine+=... command can be used to add startup parameters to the command line   
- @ServerName=... command allows you to set a label for the server in the menu (other than the server's ServerName in the server browser)
- @myVar@=... allows you to define a variable "myVar" which will replace @myVar@ found inside ini/option values

Special values for the right hand side of an assignment:
- @myVar@ is expanded with the value previously assigned to @myVar@.
- @port,10000,2 calculates a port number as 10000 + 2*(X-1), where X is the number of \[DedicatedServerX\]
- @skillClass,X converts the value X used for Toxikk's skill classes to a value for UDK's Difficulty setting
- @env,varname returns the value of the environment variable "varname"
- @id returns the X of \[DedicatedServerX\]
 

Setup
-----
See FIRST STEPS inside ServerConfig.ini and edit the necessary settings. The file will be automatically renamed to MyServerConfig.
The first time you start the .exe it will import your old server settings from TOXIKKServerLauncher\\ServerConfigList.ini into MyServerConfig.ini. 
(You can force a re-import by removing all [DedicatedServerX] sections from MyServerConfig.ini.)

Usage
-----
When you start the .exe without any command line parameters, you'll get a list of available server configurations. 
Enter the configuration ID (or multiple separated by spaces) to start server instances.  
You can also specify the configuration IDs on the command line to start the server(s) without user input.  
To auto-download updates from Steam, all TOXIKK.exe processes must be terminated first to unlock files.
Use the command line option "-h" to get a list of all command line options.


What it does
------------
The launcher copies SteamApps\\Common\\TOXIKK\\UDKGame\\Config\\Default\*.ini to a DedicatedServerX subdirectory, overwriting any existing files.
It also copies all UDK\*.ini files which don't have a corresponding Default\*.ini. (Such files are needed for custom mutators to solve a race condition 
between accessing the UDK\* file and generating it from a Default\* file.)   
It then copies all UDK\*.ini files found in the launcher's directory to the DedicatedServerX config directory.
After that, it applies the INI changes and file copy operations defined in your MyServerConfig.ini \[DedicatedServerX\] sections to the files in the subdirectory.
All settings in MyServerConfig.ini which don't refer to an INI file are used as the command line options for toxikk.exe.

ServerConfig.ini
----------------
There are a lot of comments inside the file to help you build your own config.

The \[ServerLauncher\] section contains some settings for the launcher itself, mostly directory names you need to adjust. 
If you use the file for multiple machines, you can also define a \[ServerLauncher:\<machinename\>\] section to set/override settings specific for a machine.

The \[SteamWorkshop\] section contains your login information and the workshop item numbers you want to download/update.
If you use the file for multiple machines, you can also define a \[SteamWorkshop:\<machinename\>\] section which will replace the default section as a whole for a machine.

The \[SimpleNames\] section can be used to define friendlier names to be used inside your other sections instead of the full INI path or command line option name.
There are no machine specific overrides for this section.

The \[Client\] sections contain modification that will be applied to the base configuration in TOXIKK\\Config when selected, which also affects the client configuration.  

The \[DedicatedServerX\] sections contain your actual server configurations, or more precicely the settings that need to be changed from the standard settings.  
If you use the file for multiple machines, you can also define a \[DedicatedServerX:\<machinename\>\] section to set/override settings specific for a machine.

Machine specific override sections work for [ServerLauncher], [Client], [DedicatedServerX] and [SteamWorkshop]. 
The [SteamWorkshop:\<machine\>] section replaces the default [SteamWorkshop] entirely, the other sections only override individual settings.

All other sections have no implicit meaning, but can be used with @Import=\<section-name\> to re-use common settings in multiple server configurations, i.e. for Bloodlust or Cell Capture servers.
@Import works in the Client and DedicatedServerX sections and all sections imported from there.

[Example ServerConfig.ini](ToxikkServerLauncher/ServerConfig.ini)


Compiling from source
---------------------
I used VS2015 and .NET 4.0.  
