# GrowerCutTreesPatch — build kit

Ready-to-use patch is here: https://github.com/kebabebak/HSK-Grower-Cut-Trees-Patch

Files to compile `GrowerCutTreesPatch.dll` for RimWorld HSK 1.5.

## Requirements

- [.NET SDK](https://dotnet.microsoft.com/download) (builds `net48`)
- Four reference DLLs in `libs/`

## Build

```powershell
.\build.ps1
```

Or:

```powershell
dotnet build GrowerCutTreesPatch.csproj -c Release
```

Output: `out\GrowerCutTreesPatch.dll`
