# WslContainerd.PostgreSQL

PostgreSQL container lifecycle and generic SQL access (no application-specific schema).

Repository: https://github.com/XiaEmrys/WslContainerd.PostgreSQL

Evolux business tables (conversations, training, routing) live in **`Evolux.PostgreSQL`** in the main Evolux repository.

## Dependencies

Requires **WslContainerd.Net** abstractions. In Evolux monorepo: `ProjectReference`; standalone: NuGet `0.1.0` from [WslContainerd.Net](https://github.com/XiaEmrys/WslContainerd.Net).

## Options

| Property | Default |
|----------|---------|
| `ImageName` | `postgres:15` |
| `ContainerName` | `postgresql` |
| `ApplicationDatabase` | `app` |

## Build

```bash
dotnet build WslContainerd.PostgreSQL.sln -c Release
dotnet pack WslContainerd.PostgreSQL.sln -c Release -o artifacts/nuget
```

## Sync with Evolux (subtree)

```powershell
.\scripts\wsl-containerd-postgresql-subtree.ps1 -Split
.\scripts\wsl-containerd-postgresql-subtree.ps1 -Push
```

Docs: `docs/wsl-containerd-subtree.md` in the Evolux repository.
