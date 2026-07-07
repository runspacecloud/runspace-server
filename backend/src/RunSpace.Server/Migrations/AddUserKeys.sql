-- User encryption keys table
CREATE TABLE IF NOT EXISTS UserEncryptionKeys (
    UserId INTEGER PRIMARY KEY,
    PublicKey TEXT NOT NULL,
    EncryptedPrivateKey TEXT NOT NULL,
    KeyVersion INTEGER DEFAULT 1,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (UserId) REFERENCES AuthUsers(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_user_keys_userid ON UserEncryptionKeys(UserId);
