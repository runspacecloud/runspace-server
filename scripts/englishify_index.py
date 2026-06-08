#!/usr/bin/env python3
from pathlib import Path
import shutil

path = Path("/root/RunSpace/NewServer/publish/wwwroot/index.html")

if not path.exists():
    print(f"File not found: {path}")
    raise SystemExit(1)

backup = path.with_suffix(".english.bak")
shutil.copy2(path, backup)

text = path.read_text(encoding="utf-8")

replacements = [
    ("lang=\"sv\"", "lang=\"en\""),
    ("Svenska", "English"),
    ("Hem", "Home"),
    ("Startsida", "Home"),
    ("Välkommen", "Welcome"),
    ("Logga in", "Log in"),
    ("Logga In", "Log In"),
    ("Registrera", "Register"),
    ("Skapa konto", "Create account"),
    ("Användarnamn", "Username"),
    ("Lösenord", "Password"),
    ("E-post", "Email"),
    ("E-postadress", "Email address"),
    ("Glömt lösenord", "Forgot password"),
    ("Inställningar", "Settings"),
    ("Profil", "Profile"),
    ("Sök", "Search"),
    ("Meddelanden", "Messages"),
    ("Skicka", "Send"),
    ("Laddar...", "Loading..."),
    ("Laddar", "Loading"),
    ("Försök igen", "Try again"),
    ("Tillbaka", "Back"),
    ("Nästa", "Next"),
    ("Fortsätt", "Continue"),
    ("Avbryt", "Cancel"),
    ("Stäng", "Close"),
    ("Öppna", "Open"),
    ("Radera", "Delete"),
    ("Rapportera", "Report"),
    ("Kopiera", "Copy"),
    ("Svara", "Reply"),
    ("Reagera", "React"),
    ("Online", "Online"),
    ("Offline", "Offline"),
    ("Ansluten", "Connected"),
    ("Ansluter", "Connecting"),
    ("Återansluter...", "Reconnecting..."),
    ("Frånkopplad", "Disconnected"),
    ("Kunde inte ladda", "Could not load"),
    ("Kunde inte hämta", "Could not fetch"),
    ("Kunde inte spara", "Could not save"),
    ("Kunde inte dekryptera", "Could not decrypt"),
    ("Ogiltig", "Invalid"),
    ("Ogiltigt", "Invalid"),
    ("Ogiltig förfrågan", "Invalid request"),
    ("För många förfrågningar", "Too many requests"),
    ("Åtkomst nekad", "Access denied"),
    ("Underhåll", "Maintenance"),
    ("Välj en konversation", "Choose a conversation"),
    ("Börja konversationen", "Start the conversation"),
    ("Skriv ett meddelande...", "Write a message..."),
    ("Släpp för att bifoga", "Drop files to attach"),
    ("Bilder, videor, PDF och andra filer", "Images, videos, PDFs and other files"),
]

for old, new in replacements:
    text = text.replace(old, new)

path.write_text(text, encoding="utf-8")

print("index.html patched to English")
print(f"Backup saved: {backup}")
