# Architecture

## Overview

RunSpace is a communication platform built using:

- ASP.NET Core and .NET 8
- SignalR for realtime communication
- SQLite for persistent application data
- Nginx as the web server and reverse proxy
- Cloudflare as the public edge proxy
- WebRTC for voice communication

## Request flow

```text
Client
  |
  v
Cloudflare
  |
  v
Nginx
  |
  +--> Static frontend
  |
  v
ASP.NET Core backend
  |
  v
SQLite and application storage
```

The backend service is intended to listen only on a private or loopback
interface. Public requests should pass through the reverse proxy.

## Backend structure

The primary backend project is located in:

```text
backend/
```

Important endpoint modules include:

```text
ChatReadRoutes.cs
GroupDmEndpoints.cs
GroupDmVoice.cs
GroupInviteLinks.cs
PrivateDmVoice.cs
TempChatEndpoints.cs
TempChatFriendInvites.cs
TemporaryAccountEndpoints.cs
Routes/E2eeRoutes.cs
Security/TurnstileRegistrationMiddleware.cs
```

Some older endpoints remain in `Program.cs` and should gradually be moved
into focused route modules.

## Main features

- Account registration and authentication
- Account-key login
- Two-factor authentication
- Account recovery
- User profiles
- Friend requests
- Private messaging
- Group direct messages
- Temporary accounts and chats
- Media uploads
- Realtime presence
- Voice-call signaling
- Administration and moderation
- Support tickets
- Encryption-key management
