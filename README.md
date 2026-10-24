# School Messenger (ASP.NET Core WebAPI)

A simple real-time messenger built with ASP.NET Core Web API + Entity Framework Core.  
This is my school project that I'm working on in my free time to level up my backend development skills.

## Current Features
- Registration and authentication (JWT)
- Creating private chats and real-time messaging (via SignalR)
- Sending text messages and images
- User profiles with avatars
- Basic admin panel (still under development)

(The list will be updated as new features are added)

## Tech Stack
- Backend: ASP.NET Core 8 Web API
- Database: PostgreSQL or Microsoft SQL Server (configurable via appsettings)
- Authentication: JWT + Refresh Tokens
- Real-time communication: SignalR
- Frontend (separate project for now): .NET MAUI (cross-platform desktop/mobile client)

dock: https://nas-3.gitbook.io/govor-api

Deployment: [Timeweb VPS + GitHub Actions](docs/vps-deployment.md) · [Timeweb App Platform](docs/timeweb-deployment.md).

## Server tests

Run all current server tests with `dotnet test Govor.Server.slnx -c Release`.
See [tests/README.md](tests/README.md) for the project layout, PostgreSQL setup,
coverage commands and validation limits.
