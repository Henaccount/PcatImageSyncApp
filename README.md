# PCAT Image Sync Console App

created (including this file) by GPT 5.4 pro extended (prompt attached as text), read/check/approve the code before using, really use at own risk..

<img src=pcat_image_sync_infographic.png>

(see also: <a href=PcatImageSyncFlowChart.png>PcatImageSyncFlowChart.png</a>, attached)

This project is a Windows-only .NET console application that opens Windows dialogs for:

1. selecting the root folder that contains `.pcat` files and recursively scanning all subfolders for them
2. selecting one or more source `.pcat` files
3. selecting one target `.pcat` file

It then:

- finds the support folder for each `.pcat` file (same-name folder next to the `.pcat` or under `CatalogSupportFolder`)
- ensures the `32`, `64`, and `200` folders exist
- reads `EngineeringItems` from the selected databases
- uses `PartFamilyLongDesc` and `PartSizeLongDesc` to find matching source rows
- uses `PartFamilyId` as the image GUID key
- normalizes GUID strings without leading or trailing braces
- detects target rows that are missing images in one or more of the required image folders
- copies all matching PNG files for the source `PartFamilyId` from the source support folder into the target support folder, including multiple images per family
- renames the GUID in each copied filename from the source `PartFamilyId` to the target `PartFamilyId`
- writes a log file next to the executable whenever records are ambiguous, unresolved, partially resolved, or invalid

## Project files

- `PcatImageSyncApp.csproj`
- `Program.cs`
- `DatabaseSelectionForm.cs`
- `CatalogSyncService.cs`
- `Models.cs`

## Build

Open the folder in Visual Studio or run:

```bash
dotnet restore
dotnet build -c Release
```

## Run

```bash
dotnet run -c Release
```

Or publish a standalone executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

## Notes

- The app treats multiple source hits for the same `(PartFamilyLongDesc, PartSizeLongDesc)` combination as ambiguous and logs them instead of copying.
- Rows that already have at least one image in `32`, `64`, and `200` can still receive additional matching family images when a unique source match exists.
- The app does not invent a missing support folder location. If the base support folder for a source or target catalog cannot be found, that condition is logged.
- The app creates missing `32`, `64`, and `200` folders inside an existing support folder.
