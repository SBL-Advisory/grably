<p align="center">
  <img src="assets/brand/grably-banner.png" alt="Grably - screenshots, annotations and screen recordings for Windows 11" width="720">
</p>

# Grably

Free, open-source screenshots, annotations and screen recordings for Windows 11.

Grably is in early development. The plan and progress are in [docs/PLAN.md](docs/PLAN.md); brand assets live in [assets/brand](assets/brand).

## Building

Requires Windows 11 and the .NET 10 SDK.

```
dotnet build Grably.slnx
dotnet test --solution Grably.slnx --filter-not-trait "Category=Desktop"
```

Run `src/Grably.App/bin/Debug/net10.0-windows10.0.26100.0/Grably.exe`. Grably starts in the notification area; click its icon to open the Library.

## License

[MIT](LICENSE)
