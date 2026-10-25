# dumpread

Reads a minidump and prints what a crash in this game actually needs: the
exception record, the register context, the loaded and unloaded module maps, and
a scan of the faulting thread's stack for values that fall inside a module.

It exists because no debugger is installed on the machine this mod is developed
on, and installing one to answer "is the faulting address inside a module that
was unloaded" is more setup than the question deserves. The unloaded module list
is a minidump stream; that question is a table lookup.

```
dotnet run DumpRead.cs "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY FFX&FFX-2 HD Remaster\ffx-dumps\ffx.dmp"
```

Dumps are produced by `FFX-mit-Dump-starten.cmd` in the game directory, which
sets `DOTNET_DbgEnableMiniDump`. The name must not contain `%p`: this runtime's
createdump rejects it with "The pid argument is no longer supported" and then
writes nothing at all.

Two things to read first in the output:

- **The exception code.** `0xC0000005` is the access violation this mod is
  chasing. `0xE0434352` is a managed exception and the first parameter is its
  HRESULT - `0x80004003` is a NullReferenceException, which is a different bug.
- **The unloaded module list**, and where the faulting address falls. An address
  inside an unloaded `magic_*.dll` is the overlay teardown candidate; an address
  in `FFX.exe` is not.

The image is relocated at runtime - measured at `0x00440000` rather than the
`0x00400000` the Ghidra addresses assume - so read module offsets from the map
this prints rather than subtracting a constant.
