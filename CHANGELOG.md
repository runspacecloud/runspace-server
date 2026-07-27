# Changelog

All notable changes to RunSpace are documented in this file.

This changelog was compiled from the project's historical patch notes and is ordered newest first.

> [!NOTE]
> Some original notes did not include an exact release date or version number. Those entries are marked accordingly. The source contains two separate updates labeled `v2.2`; both are preserved. Ambiguous historical wording is retained rather than silently rewritten.

## Unversioned Update — Platform Update (2026-07-12 to 2026-07-27)

### Added

- Added Group DMs with support for:
  - Group creation
  - Member management
  - Ownership transfer
  - Leaving groups
  - Group deletion
  - Custom group names
- Added Group Voice Calls using WebRTC.
- Implemented Private P2P Voice Calls.
- Added support for video and audio uploads.
- Introduced Temporary Chats improvements.
- Began development of the new RunSpace Android app.
- Added Account Key login support to the Android client.
- Created a new /donate page.
- Deployed an experimental RunSpace Onion (.onion) service.

### Security

- Improved Cloudflare protection with additional Turnstile validation.
- Expanded server hardening and firewall configuration.
- Improved encrypted media handling.
- Continued planning and research for future MLS (Messaging Layer Security) integration.
- Improved infrastructure for multi-device encryption support.

### Improved

- Improved Group DM stability and performance.
- Improved voice call reliability.
- Improved Android UI and overall user experience.
- Improved server maintenance and monitoring.
- Added automatic SQLite backups with retention.
- Optimized disk usage and cleaned server storage.
- Refined branding across the website and social media.

### Fixed

- Fixed several Android build issues.
- Fixed Group Voice synchronization issues.
- Fixed multiple server-side stability problems.
- Fixed issues related to Nginx configuration.
- Fixed several authentication and API edge cases.
- Fixed numerous internal bugs and performance bottlenecks.

### Branding

- Refined the RunSpace visual identity.
- Updated social media assets.
- Continued improvements to the RunSpace logo and overall branding.

### Thank You

- Thank you to everyone testing RunSpace and sharing feedback. Development has continued every day, even during periods without published patch notes. More updates are coming soon.

## Unversioned Update — Chat Improvements (date not provided)

- We've made several improvements to the chat experience to make messaging feel smoother, cleaner, and more privacy-focused.

### Added

- Added image previews before sending.
- Added a new attachment (+) menu for uploading images and files.
- Added image file information (name, size, type) before upload.
- Added an optional Remove metadata toggle for image uploads.
- Added full-screen image preview before sending.

### Privacy

- Images can now have their metadata removed server-side before being stored.
- Common metadata such as camera information, software, timestamps, GPS location, EXIF, XMP, and IPTC data are stripped when enabled.

### Improved

- Improved attachment composer design with a cleaner and more compact layout.
- Refined send and attachment buttons for a more polished appearance.
- Reduced excessive glow effects throughout the composer.
- Improved upload animations and image preview interactions.
- Improved image handling inside conversations.
- Improved scrolling behavior after sending messages.
- Various UI polish and performance improvements.

### Fixed

- Fixed multiple attachment preview issues.
- Fixed image upload metadata handling.
- Fixed several chat UI inconsistencies.
- General bug fixes and stability improvements.

## [3.2] (2026-06)

- RunSpace v3.2 focuses on platform stability, public profile improvements, API access information, privacy page updates, and continued progress on RunSpace Lightweight for Linux.

### API Page

- Added a new API information page.
- Users can now learn more about RunSpace API access directly from the website.
- Added information about API access requirements and usage rules.
- Added guidance for approved users and partners who want to request API access.

### Privacy Page Improvements

- Improved the Privacy Policy page layout.
- Improved readability and spacing across the page.
- Improved desktop scaling when using the sidebar layout.
- Improved the Privacy page experience across different screen sizes.

### Public Profile Improvements

- Improved public profile page consistency.
- Improved badge visibility on user profiles.
- Made more profile badges interactive.
- Improved role and verification indicators on public profiles.

### RunSpace Lightweight for Linux

- Continued improvements to the RunSpace Lightweight Linux beta.
- Improved the login experience in the Linux desktop client.
- Improved direct message loading.
- Improved conversation reliability.
- Improved message history loading.
- Improved the connection between RunSpace.cloud and the Linux desktop client.
- Improved the desktop chat layout and scrolling behavior.

### Website Experience

- Improved consistency between legal, privacy, API, and support pages.
- Improved sidebar-based page layouts.
- Improved spacing and alignment across multiple pages.
- Improved visual consistency across public-facing pages.
- Improved navigation between important platform pages.

### Platform Stability

- Stabilized backend systems for the latest platform update.
- Improved server configuration and platform reliability.
- Improved internal performance across selected platform services.
- Continued infrastructure optimizations to support future updates.

### Mobile Experience

- Improved responsive behavior on several pages.
- Improved compatibility across different screen sizes.
- Improved page scaling on smaller displays.

### Fixed

- Fixed API page loading issues.
- Fixed Privacy page width and layout issues.
- Fixed sidebar layout issues on selected pages.
- Fixed several visual inconsistencies across profile and legal pages.
- Fixed additional responsive design issues.
- Fixed issues that could cause some pages to return server errors.

### Developer Notes

- RunSpace Lightweight for Linux remains in beta.
- The current Linux desktop version is lightweight and focused on core messaging.
- A fuller desktop client with more features is planned for a future release.
- Voice calling remains in active development.
- Additional platform improvements are planned for future releases.
- Thank you for using RunSpace.

## [3.1] (2026-06)

- RunSpace v3.1 focuses on improving communication, support, and community transparency.

### Added

- Contact Page
- Added a brand new Contact page.
- Users can now contact the RunSpace team directly through the website.
- Added dedicated sections for support, business inquiries, and general questions.
- Community Rules
- Added a dedicated Community Rules page.
- Improved transparency regarding platform expectations and moderation.
- Status Page Improvements
- Expanded platform status information.
- Improved visibility of service availability and ongoing issues.

### Website Experience

- Improved footer consistency across multiple pages.
- Improved navigation between legal and support pages.
- Improved page layouts and spacing across the website.

### Security & Infrastructure

- Continued backend stability improvements.
- Improved internal handling of contact submissions.
- Additional server and infrastructure optimizations.

### Mobile Experience

- Improved mobile responsiveness on several pages.
- Improved compatibility across different screen sizes.

### Fixed

- Fixed multiple footer alignment issues.
- Fixed Terms of Service navigation issues.
- Fixed Contact page layout inconsistencies.
- Fixed various responsive design problems.
- Fixed several visual inconsistencies across the website.

### Developer Notes

- Voice calling remains in active development.
- The RunSpace QT Desktop Client continues to receive development focus.
- Additional platform improvements are planned for future releases.
- Thank you for using RunSpace.

## [3.0] (date not provided)

- RunSpace v3.0 is now live.
- This update introduces the foundation for RunSpace Premium, improves account settings, and fixes several stability issues across the platform.

### RunSpace Premium

- Premium and Premium+ are now being introduced. Users can view available plans and upgrade directly from RunSpace.

### Plans in Settings

- A new Plans section has been added to Profile Settings, making it easier to view your current plan and manage upgrades.

### Secure Payments

- Premium upgrades are handled through Stripe Checkout. Subscribed users can manage billing, payment methods, invoices, and cancellations through the billing portal.

### Improved

- Profile banners are now part of Premium
- Premium features are checked more reliably
- Account and billing pages are more stable
- The Premium page has been improved
- The Key Change page now loads correctly
- Missing icons and asset issues have been fixed
- Chat page script issues have been cleaned up

### Stability

- We also completed a platform check to make sure public pages, assets, and scripts load correctly.
- Premium will continue to receive more features over time.
- Thank you for using RunSpace.cloud.

## [2.9] — Profile Verification Update (date not provided)

- RunSpace v2.9 improves how profile verification marks work and look across RunSpace.cloud.
- What’s New
- Added cleaner verification marks next to usernames.
- Verified profiles can now display a white verification mark.
- Official RunSpace roles can now have special profile marks.
- Added support for different mark types, including Owner, Developer and Support.
- Hovering over a mark now shows what it means.
- Profile marks are now smaller and better aligned with usernames.

### Profile Improvements

- Profile headers now look cleaner and less crowded.
- Official and trusted accounts are easier to recognize.
- Team-related marks now have clearer visual differences.
- The verification system now feels more polished and easier to understand.

### Fixed

- Fixed an issue where old profile marks could still appear after being changed.
- Fixed badge data not updating correctly on some profiles.
- Improved how profile marks load on the profile page.
- Improved profile styling and spacing around usernames.

### Why This Matters

- Verification marks help members understand which profiles are verified, official or connected to the RunSpace team. This makes profiles easier to read and helps reduce confusion when viewing accounts across RunSpace.cloud.

## [2.8] (date not provided)

- We're excited to release a new update focused on improving the website experience, navigation, and overall reliability.

### Added

- Improved Legal Pages
- Fixed an issue where the Terms of Service button could redirect incorrectly.
- Updated navigation links across the website for a more consistent experience.
- Improved the Terms of Service page layout and styling.
- Better visual consistency between the Privacy Policy and Terms of Service pages.
- Website Improvements
- Refined page navigation throughout RunSpace.
- Improved link reliability across multiple sections of the website.
- Updated legal page structure for better readability.
- Stability & Reliability
- Various backend and infrastructure improvements.
- Improved platform security and internal service reliability.
- Continued work on long-term performance and stability upgrades.

### Fixed

- Fixed broken Terms of Service navigation.
- Fixed styling inconsistencies on legal pages.
- Fixed several outdated links across the website.
- Thank you for using RunSpace and helping us improve the platform.

## [2.7] (2026-06)

### User Experience

- Fixed an issue where the chat could unexpectedly jump while messages were being sent.
- Improved chat rendering and scrolling behavior.
- Fixed several profile navigation inconsistencies.
- Corrected "Profilee" typo across the interface.
- Updated profile shortcuts to redirect to account settings where appropriate.
- Improved navigation consistency across desktop and mobile layouts.

### Chat

- Improved message rendering performance.
- Reduced unnecessary chat reloads after sending messages.
- Better handling of temporary messages before server confirmation.
- Improved conversation stability during active chats.

### Profiles

- Improved profile navigation flow.
- Fixed incorrect profile button behavior on the home page.
- Improved profile settings accessibility.
- Improved public profile routing.

### Security

- Continued improvements to device trust systems.
- Continued improvements to rate limiting and abuse prevention systems.
- Internal security maintenance and backend cleanup.

### Infrastructure

- Frontend cleanup and navigation refactoring.
- Additional codebase maintenance and optimization.
- Improved overall platform stability.

### Known Issues

- Some older pages may still contain legacy navigation behavior.
- Additional chat improvements are planned for future releases.

### Thank You

- Thank you for helping test RunSpace and reporting bugs. Every report helps improve the platform for everyone.
- RunSpace Team

## [2.6] (2026-06-08)

### Added

- Private calls are now in the testing phase. It would be prudent to temper one's expectations.

### Improved

- It is evident that the system is characterised by enhanced reliability and stability.
- The security protocol has been updated to its most recent iteration.

### Notes

- It has been determined that the term of service is not currently operational. This issue is being addressed and a resolution will be implemented in the near future.
- IOS & ANDROID : It is evident that the functionality of background playback on YouTube, in addition to controls pertaining to the lock screen, remains constrained by browser restrictions. In the event that alternative sources are not available, it is recommended that local files are utilised as a workaround.

## [2.5] (2026-05-24)

### Added

- YouTube playback now uses browser IFRAME API (no server-side yt-dlp)
- Volume slider works significantly better with YouTube
- Mobile improvements: Progress bar, seeking, wake lock, and larger touch targets

### Improved

- Faster and more reliable playback on /music
- More stable system and backend

### Fixed

- Fixed 404 errors and yt-dlp fetch failures when playing music
- Fixed volume slider only affecting system volume
- Fixed broken backend build caused by duplicate files
- Added correct version number

### Notes

- Please hard-refresh /music after updating
- iPhone: YouTube background playback and lock-screen controls are still limited due to browser restrictions. Use other sources or local files as workaround.

## [2.4] — RunSpace Music (2026-05-21)

- Fully rebuilt music player now live at runspace.cloud/music

### Added

- New design — cleaner, darker, sharper
- Progress bar with click-to-seek and timestamps
- Volume control tied to actual playback
- Toast notifications (no more annoying pop-ups)
- Remove tracks directly from the playlist
- Keyboard shortcuts — Space to play/pause, Ctrl+→/← to skip
- Dynamic greeting based on time of day
- Improved Recently Played cards with hover animations

### Unchanged

- YouTube & Spotify support via URL
- IndexedDB playlists (saved locally in your browser)
- Mobile friendly
- Have any issues? Contact @mx403 or @NulliGit. Always check their appearance on discord if they are offline or online!

## [2.3] (2026-05-18)

### RunSpace System (Bot Detection)

- Introduced RunSpace System for detecting:
  - fake accounts
  - bot traffic
- Improves backend filtering of automated requests

### Page Stability

- Fixed CSS loading issues across multiple pages
- All main pages on runspace.cloud are now fully functional
- Improved consistency in layout rendering

### Theme System

- User-selected themes now apply consistently across all pages
- Fixed issue where some pages did not inherit global theme settings

### Improved

- Improved frontend stability and rendering performance
- Better consistency between routes and page templates
- Reduced visual glitches caused by missing or broken CSS links

### Fixed

- Fixed broken styling on several legacy pages
- Fixed inconsistent theme application across navigation
- Fixed minor UI layout shifting on page load

### Notes

- This update focuses on stability and visual consistency
- No breaking changes introduced
- Backend systems remain fully compatible with previous versions

## [2.2] — Registration Update (2026-05-10)

### Registration

- Fixed css problem.

### Added

- The account creation page has been given a visual refresh; cleaner layout, better spacing, and a more polished dark theme that feels more consistent with the rest of RunSpace. Everything works the same as before, just looks better.
- 🔐 Security Update • Key Passphrase Rotation
- RunSpace now supports secure passphrase rotation for your .key file.
- If you want to change the passphrase protecting your key file, you can now request a secure one-time link from staff.

### How It Works

- Contact @mx403
- You’ll receive a private one-time security link
- Your .key file is decrypted and re-encrypted locally in your browser
- Your passphrase never leaves your device
- Your AccountKey stays exactly the same

### Principle

- Your keys. Your device. Your control.

## [2.2] — UI & Theme Overhaul (2026-05-06)

### Added

- Global theme support across all pages — Dark, Midnight, OLED and Light modes
- Falling star background effect on login, profile and settings pages
- Login page now redirects automatically to /login when not logged in
- Profile page shows a Settings button for the logged-in user
- Sponsors page created at /sponsors
- Cases Admin page at /cases-admin for admins to manage support tickets

### Improved

- Support page rebuilt with FAQ, ticket categories, priority levels, reply system and support hours
- Consistent theming across all pages — sidebar, topbar and content areas now follow the selected theme
- Music player dark playlist tab fixed for light mode

### Fixed

- Login redirect loop removed
- Star animation fixed on login page (left position was missing)
- TOS and profile pages now respond correctly to theme changes

## [2.1] — Platform Update (2026-04-25)

### Security

- Profile data leak fixed — Username, avatar and account details are no longer embedded in the page before you log in. Nothing is shown until your session is verified.
- Admin controls hidden by default — Admin links in the sidebar are now hidden on page load and only appear after authentication. They also hide correctly if the connection drops.
- GIF API secured — The GIF search API key has been moved to the server. It is no longer visible in page source.

### Fixed

- Chat avatars — Fixed bug where conversation list avatars would disappear or render incorrectly when opening a chat. Letter avatars are now cached and reused correctly.
- E2E encryption — Fixed critical bug where encrypted messages and images were not decrypted before rendering. Messages and uploaded images now display correctly in chat.
- Image upload — Improved error feedback when an image upload fails. A toast message now shows the reason instead of a generic browser alert.
- Memory leak — Fixed a memory leak in chat where a new canvas element was created every time an avatar was rendered. Avatars are now cached.

## [2.0] — Platform Update (2026-04-21)

### Added

- Music Player rebuilt — Now supports YouTube, local files and Spotify links. Fixed autoplay policy issues.
- Legal pages — Added privacy.html and terms-of-service.html in the RunSpace theme.
- News images — Articles now support multi-image upload with lightbox viewing and admin controls.
- Mobile experience — Redesigned mobile views for homepage, profile, chat, spaces and inbox. PWA cache bumped to v1.0.4.

### Fixed

- E2E encryption — Fixed critical bug where all encrypted messages rendered as raw base64. Messages now decrypt correctly.
- Message deletion — Fixed bug allowing any user to delete anyone else's messages.
- i18n / SVGs — Fixed i18n.js overwriting inline SVG icons with textContent.

## [1.9] — Support Inbox (2026-04-19)

### Support Inbox

- We've rebuilt our support system from the ground up to give you a faster, clearer way to get help.

### Added

- My Tickets page — A new page at /my-tickets.html where you can view all your support tickets, read responses from our team, and reply directly.
- Two-way conversations — Every ticket now supports a full back-and-forth with support, displayed in a clean chat-style thread.
- Real-time updates — You'll receive instant notifications whenever support replies or updates your ticket.
- Unread indicator — A counter in the sidebar lets you know when you have new responses waiting.
- Clear status at a glance — Every ticket shows its current state: Open, In Progress, Waiting for You, Resolved, or Closed.
- Faster response times — Our team now has better tools to prioritize and respond to tickets, so you'll hear back quicker.
- Tip: Use Ctrl+Enter (or Cmd+Enter on Mac) to quickly send a reply from any ticket.

## [1.8] (2026-04-17)

### Authentication

- Fixed critical bug where users were logged out immediately after login
- Login sessions now persist correctly and remain stable across page navigation
- Session cookies now work reliably across browser restarts
- Added cache prevention to avoid stale login states

### Stability

- Improved backend session handling for better reliability
- Authentication system simplified and stabilized

## [1.7] — RunSpace Market (2026-04-14)

- New Feature: RunSpace Market
- A brand new marketplace for buying and selling scripts, completely separate from the main RunSpace chat platform.

### Market Platform

- New subdomain: market.runspace.cloud
- Browse, search and filter scripts by category
- Script detail pages with descriptions, file lists, and reviews
- Download purchased scripts directly

### Separate Account System

- Standalone market accounts with email/password registration
- Or link your existing RunSpace account with one click
- Seller profiles at /profile/{userId} showing:
  - Total sales count ("Sold X scripts")
  - Published scripts
  - Buyer feedback and ratings

### For Sellers

- Upload scripts as ZIP files (max 50 MB)
- Automatic security scanning before review
- Set pricing (free or paid in USD)
- Add description, category, language, requirements
- Support contact info

### For Buyers

- Browse free and paid scripts
- Personal library of purchased scripts
- Leave feedback on sellers after purchase
- Re-download anytime from your library

### Security

- All uploads scanned for malicious content
- Trust scoring system
- Separate authentication cookies for market

### Technical Details

- New database sets
- New API endpoints for market
- SSL certificate for market.runspace.cloud tools, debuggers, and memory editors. Free download!

## [1.6] (2026-04-13)

### Visual

- Star animation (falling stars) added to login, register, index, privacy, terms, about, news and download pages
- Grid background removed from all pages — clean dark space background throughout
- Background color unified to #07070d across all public pages
- Sidebar and header on privacy/terms/about/news/download now match the background

### Login & Register

- Error messages now appear as toast notifications in the bottom-right corner instead of inline red boxes
- "I agree to Terms of Service and Privacy Policy" checkbox added to both login and register
- Terms/privacy links now open in a new tab
- Login error message generalized — no longer leaks whether a username exists

### Chat

- Grid background removed, star animation added
- User search fixed — now works correctly while typing
- Curly quotes fixed that caused a SyntaxError

### Download Page

- Full bilingual support — Swedish or English based on the visitor's system language

## [1.5] (2026-04-12)

### End-to-End Encryption in the Browser

- The web client now speaks the same encryption language as the desktop app. Messages are encrypted with RSA-OAEP + AES-256-GCM directly in your browser using the Web Crypto API. Your keypair is generated once and stored in IndexedDB no server ever sees your private key. If someone sends you a message from the desktop app, you can now actually read it.

### Old Encrypted Messages Cleaned Up

- 82 messages that were encrypted with old or mismatched keys were removed from the database. They were unreadable anyway this clears out the noise.

### Servers

- You can now create servers from the web client. Give it a name, add channels, invite people. The rail on the left shows your servers as icons, same as the desktop app.

### Download Page

- Completely rebuilt. English only, cleaner layout, platform cards showing what's available and what's coming. There's a live countdown to the open source release on April 20th at 20:00 Swedish time.

### Fixed

- Fixed an issue where incoming encrypted messages from the desktop client would show raw base64 ciphertext instead of the actual message. The web client now correctly finds and decrypts the AES key for your device from the recipientKeys array.

## [1.4] (2026-04-11)

### Chat

- Quick terminal : press Ctrl+Alt+T anywhere on RunSpace to open a quick navigation bar. Type /chat, /settings, /profile etc. to jump there instantly
- Fast DM :type / in the message box, then /private dm {username} to open a DM without searching
- Paste files: paste images and files directly into chat with Ctrl+V

### Security & Privacy

- User ID : every account now has a unique 32-character ID, right-click a conversation to copy it
- Stay logged in: you are no longer logged out when the server updates
- Multiple tabs : having RunSpace open in multiple tabs no longer counts as multiple devices

### Moderation

- Automatic spam detection — attempts to mass-DM multiple users at once are blocked
- Expanded username filter — hate speech and admin impersonation are blocked at registration

### Server Bots (Coming Soon)

- Bot system is in early development: server owners will be able to create custom bots using a Python SDK.
- Features will include message commands, auto-moderation, IP blacklisting and word filters
- Not fully functional yet full release in a future update

## [1.3] (2026-04-09)

### Security

- New trust system based on account age, email verification, 2FA, device, and behavior
- Trust penalties for violations with automatic recovery over time
- Device intelligence detects suspicious activity (IP hopping, multi-accounting)
- Escalating cooldowns for repeated violations

### Profiles

- Trust badge with clear explanations
- Public profiles show “Restricted” when applicable

### Other

- Music player at /music (MP3, FLAC, WAV, OGG, AAC)
- Clean URLs (no .html)

## [1.2] — Internal Improvements Update (2026-04-07)

- RunSpace Chat Internal Improvements Update
- This update focuses on stability, consistency, and improving how the chat behaves under the hood.

### Improved

- Reworked message rendering flow to reduce unnecessary full re-renders
- Introduced more incremental DOM updates for smoother message handling
- Improved scroll stability during conversation changes and history loading
- Cleaner separation between message state and rendering logic
- More consistent handling of message states (pending, sent, failed)
- General codebase cleanup for better maintainability and future development

### Result

- Chat feels more stable, more predictable, and easier to build on going forward without any changes to the UI.
- More improvements coming soon.

## [1.1] (2026-04-03)

### Profile

- NEW: Profile Banners: Upload a custom banner image (up to 8 MB) on your profile page. Visible on both your own profile and public profiles.
- NEW: Banner management; Upload, preview, and remove banners from profile settings.

## [1.0] (2026-04-03)

### Chat

- NEW: Group DMs — Create group conversations with up to 15 people directly from the conversation list.
- NEW: Context Menu — Right-click usernames to view profile, send message, copy name, or report.
- NEW: Group Context Menu — Right-click groups to leave or delete (owner only).
- NEW: Chat Polish — Message fade-in animations, hover effects, Discord-style scrollbars, dynamic placeholder, online status indicator.

### Profile

- NEW: Social Links on Public Profiles — Discord, Steam, GitHub and other links now visible to everyone.

### Patch Notes

- NEW: Accordion-style patch notes with expandable versions.

## [0.9] (2026-04-02)

### Added

- GIF Picker – "+" button replaces 📎 with menu: Attach file / GIF. Upload, favorite, and send GIFs with E2E encryption.
- Custom Notification Sounds – 6 built-in sounds + upload your own (MP3/WAV/OGG). Volume slider included.
- Enhanced Settings – 9 tabs: Voice, Notifications, Profile, Account & Security, Appearance, Chat, Accessibility, Privacy, Advanced.
- Profile Links – Add up to 8 social links (GitHub, Discord, Steam, YouTube, Twitter, Instagram, TikTok, Twitch, Spotify, email, website). Visible on both profile and public profile pages.
- Admin Panel on Public Profiles – Admins can manage badges and user status directly from any user's public profile page.
- Badge System – Colorful pill-shaped badges with icons and gradients. 10 preset badges: verified, vip, developer, moderator, early-supporter, contributor, bug-hunter, partner, booster, tester.
- Expanded Emoji Reactions – 30 emojis available for message reactions, up from 8.

### Improved

- SignalR bundled locally for faster international access
- Forwarded headers enabled for correct IP logging
- Server settings tabs hidden from DM view
- Message hover toolbar redesigned (Discord-style)

### Fixed

- Relaxed threat detection for international users (reduced false positives)
- Fixed composer blocking when view state was incorrect
- Session timeout extended from 30 min to 2 hours
- Fixed reply/react buttons not appearing on hover (CSS specificity fix)

### New API Endpoints

- POST /api/admin/badges/{username} – Update user badges (admin only)
- POST /api/admin/status/{username} – Update user status (admin only)
- Profile endpoints now support links field

### Known Issues

- Reply hover toolbar positioning needs further testing
- file-upload.js has legacy function references
- Duplicate linkModal elements in HTML

## [0.8] (2026-03-31)

### Device Key Sync

- Link encryption keys between devices using a 6-digit code
- New device shows code, existing device approves transfer
- Private key encrypted with PBKDF2 + AES-256-GCM during transfer
- Server never sees the plaintext key
- Persistent device IDs — no more key loss on app restart

### Custom Server Roles

- Create custom roles with name, color and permissions
- Role hierarchy with drag-and-drop ordering
- Per-role permissions: manage members, channels, kick, invite, pin messages
- Default roles auto-created: Owner, Admin, Moderator, Member
- Role colors shown on usernames in member list
- Discord-style member management panel with search

### Dynamic News System

- News loaded from API instead of hardcoded HTML
- Admin panel to publish news directly from the web
- 4-language support: Swedish, English, French, Russian
- Dashboard widget auto-updates when switching language
- Delete articles with one click (admin only)

### Admin Roles

- Multiple admin accounts supported
- Admin-only news publishing and deletion

### Improved — Encryption

- Fixed device ID persistence (was generating new UUID every launch)
- Desktop client (Qt/C++) now saves device ID to disk
- Cleaned orphaned device keys from database

### Improved — Profile Page

- Removed nationality and language sections
- 2FA promoted to prominent position
- Compact layout inspired by GitHub dashboards
- Click avatar to change profile picture

### Improved — Login Page

- Language fallback chain: selected → English → Swedish
- No more raw i18n keys when using French or Russian

### Fixed — News Widget

- Dashboard news widget now loads dynamically from API
- Language switching updates news in real-time

### Fixed — Chat Encryption

- Messages no longer fail to decrypt after app restart
- All device keys reset to clean state

## [0.7] (2026-03-30)

- ENCRYPTION RESET
- All DM messages have been cleared due to broken encryption keys
- All sessions logged out; please log in again
- New encryption keys are generated automatically on login
- This permanently fixes the "Could not decrypt" errors

### Screen Sharing

- Share your screen during private calls
- Quality presets: 8K UHD 30 fps, 4K 30fps, 1440p 144fps, 1080p 240fps
- Fullscreen viewer with auto-hiding controls (ESC to exit)
- Optimized for gaming; motion encoding + up to 40 Mbps bitrate

### File Sharing

- Send any file type in DMs; ZIP, PDF, documents, videos, audio
- Videos and audio play inline with embedded player
- GIFs display animated
- Max 50 MB per file

### Nationality & Languages

- Set your nationality with flag emoji on your profile (250 countries)
- Select languages you speak (Swedish, English, French, Russian)

### Internationalization

- Full site translation — Swedish, English, French, Russian
- Language switcher on every page (SV / EN / FR / RU)

### TURN Server

- Added relay server for better voice/call connectivity
- Fixes issues with mobile networks, VPNs, and strict NATs

### Improved — Call UI

- Call bar collapses to top — chat while in a call
- Quality selector appears when clicking screen share
- Auto-reconnect call on page refresh
- Ringtone now stops properly when answered

### Improved — Chat UI

- Compact sidebar layout
- Scrollable conversation list
- Fixed message bubble width issues
- Removed voice channels from DM sidebar (now group-only)

### Improved — Voice Quality

- Low-latency optimizations (zero jitter buffer)
- Live edge sync for screen sharing
- VP9 codec prioritization

### Fixed

- DOCTYPE added to chat page
- ASCII-safe JavaScript files (no more encoding corruption)
- Removed stale script references
- Cleaner encryption key management
- Do you have any bugs? Report them in ⁠feedback !

## [0.6] (2026-03-29)

### Groups

- Create private groups with invite-only access
- Text channels — real-time messaging with group members
- Voice channels — WebRTC mesh audio (up to 6 users)
- Channel management — create/delete text and voice channels
- Member management — invite, kick, and role assignment
- Group settings — rename, description, danger zone (leave/delete)

### Private Calls

- One-on-one voice calls directly from DM conversations
- Incoming call notifications with accept/reject
- Screen sharing during calls — up to 4K 60 fps
- Full call controls — mute, deafen, screen share, hang up
- 30-second ring timeout with auto-cancel
- Call duration timer

### Invite System

- Search and invite users to your groups
- Pending invites shown in DM sidebar
- Accept or decline invitations with one click

### Improved — Voice Quality

- Screen share upgraded to 4K 60 fps at 15 Mbps
- Motion-optimized encoding for gaming/video content
- VP9 codec prioritization for better compression

### Improved — Chat

- Reply-to-message now works correctly via SignalR
- Verified badge (purple checkmark) on profiles

### Fixed — Server Stability

- Auto-restart on crash (systemd Restart=always)
- Removed stale chatt_inject.js reference
- Fixed encoding issues in JavaScript files (ASCII-safe)

## [0.5] — Secure Chat Update (date not provided)

- Your conversations are now private by default.
- Private messaging has been upgraded with end-to-end encryption.
- All new messages are secured using AES-GCM with modern key exchange.
- Messages are no longer stored in plaintext, and both sender and recipient can safely decrypt their chat history.
- Note: If you experience issues sending messages, log out and log back in to refresh your encryption keys.

## [0.4] — Account, Profile & Chat Update (date not provided)

- This update focuses on account flow, profile improvements, settings cleanup, and major chat system fixes.

### Added

- New combined login and register page in the RunSpace theme
- Cleaner and more user-friendly account access flow
- Profile bio editing directly in profile.html
- Backup code section added to settings interface
- Chat conversation history endpoint
- Latest conversations endpoint for chat

### Improved

- Full redesign of login.html to better match the RunSpace style
- settings.html was rebuilt to be cleaner and easier to use
- profile.html now handles bio editing in a better place
- Better chat validation and message handling
- Improved realtime chat stability
- Better error handling for private messaging
- Better message limits and recipient checks in chat
- General frontend consistency across account-related pages

### Fixed

- Fixed broken bio editing placement
- Fixed settings page issues related to removed bio handling
- Fixed chat frontend using incorrect SignalR event and method names
- Fixed chat sending logic to better match backend behavior
- Fixed duplicate and unstable chat message flow
- Fixed invalid/private chat state issues
- Fixed sending to empty or invalid users
- Fixed self-message attempts in chat
- Fixed messaging banned users
- Fixed empty message sending

### Changed

- Bio editing has been moved from settings.html to profile.html
- Account access is now handled in one simpler page
- Chat system now follows the current backend structure more correctly

### Known Issues

- Some chat edge cases may still need polishing
- More account and messaging improvements are still in progress

## [0.3] (date not provided)

### Added

- New user-focused profile view (admin features are now hidden)
- Avatar upload system (profile pictures)
- Improved navigation between pages

### Changed

- Updated UI/UX for a cleaner and more modern look
- Improved profile layout and structure
- Optimized frontend-backend communication for profile data

### Fixed

- Fixed issue where avatar upload returned 404
- Minor bugs in profile.html
- Fixed 503 bad gateway on login.

## [0.2] (date not provided)

### Added

- Added user search functionality directly on the homepage
- Introduced admin panel for managing user roles and badges
- Enabled custom badges that can be assigned to any user
- Added settings page with extended profile customization options
- Implemented patch notes system for dynamic updates

### Improved

- Improved authentication flow (reduced random logouts)
- New users are now automatically verified upon registration
- Enhanced UI across multiple pages (profile, index, settings)
- Improved navigation and overall user experience
- Optimized API structure for better performance and stability

### Security

- Added encryption layer for chat system
- Improved cookie authentication handling (more stable sessions)
- Strengthened backend validation for user actions
- Reduced risk of unauthorized access in admin features

### Fixed

- Fixed issue where users could not find themselves in search
- Resolved multiple 404 errors (profile routes, avatar upload, API endpoints)
- Fixed login session issues causing instant logout
- Corrected routing inconsistencies between frontend and backend.

## [0.1] (date not provided)

- New and improved Designer with a much cleaner and more intuitive UI
- You can now choose project type, not just language
- Supported languages: Python, HTML, C++, C#, R, Ruby, Cython, Rust
- Designer now creates real projects on the backend
- Projects are auto-generated with starter files, README, and configuration
- Improved Control Center with better navigation and usability
- Enhanced user search functionality
- Patch notes are now displayed more clearly in the dashboard
- Chat system has been stabilized and improved
- File uploads in chat have been added
- Better error handling across the platform
- Fixed multiple backend issues and 404 errors
- Improved overall system structure for future updates
