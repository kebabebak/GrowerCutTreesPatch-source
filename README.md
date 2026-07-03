# GrowerCutTreesPatch — build kit

Ready-to-use patch is here: https://github.com/kebabebak/HSK-Grower-Cut-Trees-Patch

Files to compile `GrowerCutTreesPatch.dll` for RimWorld HSK 1.5.

## Requirements

- [.NET SDK](https://dotnet.microsoft.com/download) (builds `net48`)
- `WorkTab.dll` in `libs/` (Harmony and RimWorld refs come from NuGet)

## Build

```powershell
.\build.ps1
```

Or:

```powershell
dotnet build GrowerCutTreesPatch.csproj -c Release
```

Output: `out\GrowerCutTreesPatch.dll`
