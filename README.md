# Ap.Control

## How to use

You can download the latest release from the [releases page](https://github.com/Alias-Cynestal/ArchipelagoControlClient/releases/). There are two executables that are necessary for the Archipelago to be ran properly: the patcher and the client.

## Patcher
To use the patcher, open a Terminal window and run the following command:
```cmd
Ap.Control.Patcher.exe apply all
```
This will modify your game files to be compatible with the Archipelago client. You can always run the patcher again if you need to revert the changes with the following command:
```cmd
Ap.Control.Patcher.exe restore all
```
For more information on the patcher, you can run the following command:
```cmd
Ap.Control.Patcher.exe --help
```
### Why do I need to patch my game?
Great question! The patcher modifies your game files for two specifics reasons. The first is to allow a new way of controlling the sectors available, which means that they can be unlocked in the Archipelago.
The second reason is to lock the normal way of unlocking weapons (the Astral Constructs menu), which means you'll need to use the Archipelago to unlock them.

## Client
To run the client, make sure that you have started a new Control game. Open a Terminal window and run the following command:
```cmd
Ap.Control.exe --server <url> --username <name> [--password <pass>]
```
This should connect the multiples processes that allow to read and write inside the game. As a note, you should put the prefix ws:// or wss:// before the server url, since it is required to connect to the server.

## Note on location unlocking

I do know that locations do not unlock automatically. That is because the location reading is based on the save data of the game which is not always updated in real time. However, it should always update eventually.

## I found a bug, what should I do?
Bugs fall into two categories.
- If it is a bug related to world generation or a logic problem in the order in which items can be obtained, then the issue belongs to the apworld project. In that case, you can open an issue on that page instead of this one.
- If it is a bug related to anything else (items not being granted properly, locations not being detected, etc.), it most likely belongs to the client. In that case, you can open the issue here and I'll look at it eventually.
- Either way, if you prefer, there is a Control thread on the AP After Dark Discord server. You can post your problem there; I check it regularly.

In any case, if you have questions or want to contribute to the project, don't hesitate to contact me!