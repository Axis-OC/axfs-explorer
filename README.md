# AXFS Explorer

![WinUI 3](https://img.shields.io/badge/WinUI_3-blue) ![.NET](https://img.shields.io/badge/.NET-8.0-purple)

A Windows desktop tool for browsing, editing, and managing AxisOS disk images that use the AXFS v2 filesystem and Amiga-style RDB partition tables. Because otherwise you'd have to boot AxisOS itself just to copy files on and off the disk image. And suffer. Bruh.

## What is this?

AxisOS is a custom operating system built for [OpenComputers](https://github.com/MightyPirates/OpenComputers) (a Minecraft mod that adds programmable computersssssssss). It uses its own filesystem - **AXFS v2** — stored inside virtual disk images with Amiga-style RDB partition tables.

AXFS Explorer lets you work with these disk images from Windows: open them, browse the file tree, view and edit Lua scripts with syntax highlighting, import/export files, inspect inodes, and manage partitions — without needing to boot AxisOS itself.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) and the Windows App SDK workload.

```
dotnet build AxfsExplorer.sln
```

## Usage

1. **Open** an existing `.img` or `.bin` disk image (compressed `.bin` files are auto-extracted) (probably)
2. **Create** a new image with a formatted AXFS v2 volume and RDB partition table
3. Browse directories, double-click files to view/edit, drag & drop to import

Files can also be imported/exported in bulk, and the built-in editor supports Lua syntax highlighting, find & replace, and hex view.

## Filesystem Overview

AXFS v2 is a small extent-based filesystem designed for constrained environments (4KB EEPROM boot code, 512-byte sectors). Key properties:

- **Superblock** with CRC32 integrity check and generation counter (dual-copy for crash safety)
- **Extent-based** allocation with indirect block support for larger files
- **Inline data** for files ≤52 bytes (stored directly in the inode)
- **Amiga RDB** partition table with linked-list partition entries and block checksums
- Optional **EFI partition** for encrypted boot chains with HMAC-SHA256 key derivation

The boot EEPROM (`axfs_boot.lua`) reads the RDB, locates the AXFS partition, and loads the kernel - all in pure Lua within a 4KB size limit (no).


## Linux when?

When? Fuck if I know. Maybe if someone contributes a Linux version, or if I get bored enough to do it myself. The code is pretty platform-agnostic, so it shouldn't be too hard to port - just need to replace the WinUI 3 UI layer with something cross-platform like Avalonia or MAUI.

## License

See repository root for license information. Do we even need one? I guess if you want to fork it and add features, go ahead. Just don't make it worse than it already is.