-- ============================================================
-- FullSumppot PostgreSQL Schema (Neon)
-- Run this once after creating your Neon database
-- ============================================================

CREATE TABLE IF NOT EXISTS users (
    user_id              SERIAL PRIMARY KEY,
    username             VARCHAR(30)  NOT NULL UNIQUE,
    email                VARCHAR(255) NOT NULL UNIQUE,
    password_hash        TEXT         NOT NULL,
    content_niche        VARCHAR(50),
    role                 VARCHAR(20)  NOT NULL DEFAULT 'USER',
    is_verified          BOOLEAN      NOT NULL DEFAULT FALSE,
    is_email_verified    BOOLEAN      NOT NULL DEFAULT FALSE,
    is_phone_verified    BOOLEAN      NOT NULL DEFAULT FALSE,
    primary_contact_type VARCHAR(10)  NOT NULL DEFAULT 'EMAIL',
    phone_number         VARCHAR(20)  UNIQUE,
    available_points     INT          NOT NULL DEFAULT 100,
    avatar_url           TEXT,
    created_at           TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS user_emails (
    id         SERIAL PRIMARY KEY,
    user_id    INT          NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    email      VARCHAR(255) NOT NULL UNIQUE,
    is_primary BOOLEAN      NOT NULL DEFAULT FALSE,
    is_verified BOOLEAN     NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS email_otps (
    otp_id         SERIAL PRIMARY KEY,
    email          VARCHAR(255) NOT NULL,
    otp_code       VARCHAR(6)   NOT NULL,
    purpose        VARCHAR(30)  NOT NULL,
    expires_at     TIMESTAMPTZ  NOT NULL,
    used           BOOLEAN      NOT NULL DEFAULT FALSE,
    wrong_attempts INT          NOT NULL DEFAULT 0,
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_email_otps_lookup ON email_otps (email, purpose, used, expires_at);

CREATE TABLE IF NOT EXISTS phone_otps (
    otp_id         SERIAL PRIMARY KEY,
    phone_number   VARCHAR(20)  NOT NULL,
    otp_code       VARCHAR(6)   NOT NULL,
    purpose        VARCHAR(30)  NOT NULL,
    expires_at     TIMESTAMPTZ  NOT NULL,
    used           BOOLEAN      NOT NULL DEFAULT FALSE,
    wrong_attempts INT          NOT NULL DEFAULT 0,
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_phone_otps_lookup ON phone_otps (phone_number, purpose, used, expires_at);

CREATE TABLE IF NOT EXISTS follows (
    follow_id    SERIAL PRIMARY KEY,
    follower_id  INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    following_id INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    status       VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(follower_id, following_id)
);

CREATE TABLE IF NOT EXISTS communities (
    community_id SERIAL PRIMARY KEY,
    name         VARCHAR(100) NOT NULL,
    description  TEXT,
    niche        VARCHAR(50)  NOT NULL,
    created_by   INT          NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    banner_url   TEXT,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS community_members (
    id           SERIAL PRIMARY KEY,
    user_id      INT NOT NULL REFERENCES users(user_id)      ON DELETE CASCADE,
    community_id INT NOT NULL REFERENCES communities(community_id) ON DELETE CASCADE,
    joined_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(user_id, community_id)
);

CREATE TABLE IF NOT EXISTS links (
    link_id      SERIAL PRIMARY KEY,
    title        VARCHAR(200) NOT NULL,
    url          TEXT         NOT NULL,
    community_id INT          NOT NULL REFERENCES communities(community_id) ON DELETE CASCADE,
    user_id      INT          NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    clicks       INT          NOT NULL DEFAULT 0,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS link_clicks (
    click_id        SERIAL PRIMARY KEY,
    link_id         INT         NOT NULL REFERENCES links(link_id) ON DELETE CASCADE,
    clicker_user_id INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    referrer_page   TEXT,
    click_count     INT         NOT NULL DEFAULT 1,
    clicked_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(link_id, clicker_user_id)
);

CREATE TABLE IF NOT EXISTS link_likes (
    like_id    SERIAL PRIMARY KEY,
    link_id    INT         NOT NULL REFERENCES links(link_id) ON DELETE CASCADE,
    user_id    INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(link_id, user_id)
);

CREATE TABLE IF NOT EXISTS link_comments (
    comment_id SERIAL PRIMARY KEY,
    link_id    INT         NOT NULL REFERENCES links(link_id) ON DELETE CASCADE,
    user_id    INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    content    TEXT        NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS notifications (
    notification_id SERIAL PRIMARY KEY,
    user_id         INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    sender_id       INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    type            VARCHAR(50) NOT NULL,
    message         TEXT        NOT NULL,
    is_read         BOOLEAN     NOT NULL DEFAULT FALSE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS conversations (
    conversation_id SERIAL PRIMARY KEY,
    is_active       BOOLEAN     NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS conversation_participants (
    id              SERIAL PRIMARY KEY,
    conversation_id INT NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
    user_id         INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    UNIQUE(conversation_id, user_id)
);

CREATE TABLE IF NOT EXISTS messages (
    message_id      SERIAL PRIMARY KEY,
    conversation_id INT         NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
    sender_id       INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    content         TEXT        NOT NULL,
    is_read         BOOLEAN     NOT NULL DEFAULT FALSE,
    sent_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS message_requests (
    request_id   SERIAL PRIMARY KEY,
    sender_id    INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    recipient_id INT         NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    first_message TEXT       NOT NULL,
    status       VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS auth_events (
    event_id   SERIAL PRIMARY KEY,
    user_id    INT,
    event_type VARCHAR(50)  NOT NULL,
    detail     TEXT,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
