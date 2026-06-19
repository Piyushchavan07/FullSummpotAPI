-- ============================================================
-- FullSumppot Demo Seed Script
-- Run this in Oracle SQL Developer or SQL*Plus
-- All accounts use password: demo123
-- ============================================================

-- Clean existing demo data first (safe to re-run)
DELETE FROM NOTIFICATIONS  WHERE USER_ID   IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%@demo.fullsumppot.com');
DELETE FROM LINK_CLICKS    WHERE CLICKER_USER_ID IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%@demo.fullsumppot.com');
DELETE FROM LINK_LIKES     WHERE USER_ID   IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%@demo.fullsumppot.com');
DELETE FROM LINK_COMMENTS  WHERE USER_ID   IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%@demo.fullsumppot.com');
DELETE FROM LINKS          WHERE USER_ID   IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%@demo.fullsumppot.com');
DELETE FROM FOLLOWS        WHERE FOLLOWER_ID IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%@demo.fullsumppot.com')
                              OR FOLLOWING_ID IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%@demo.fullsumppot.com');
DELETE FROM COMMUNITY_MEMBERS WHERE USER_ID IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%@demo.fullsumppot.com');
DELETE FROM COMMUNITIES    WHERE CREATED_BY IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%@demo.fullsumppot.com');
DELETE FROM USERS          WHERE EMAIL LIKE '%@demo.fullsumppot.com';
COMMIT;

-- ============================================================
-- USERS (password = demo123 for all)
-- ============================================================
INSERT INTO USERS (USERNAME, EMAIL, PASSWORD_HASH, CONTENT_NICHE, ROLE)
VALUES ('demo', 'demo@fullsumppot.com',
        '100000:eFNdkcilTyEAvqY9T0W7IQ==:lQSM35b8xjmnBoZjtK/kbTm0T5OR1hqCeAV5Gmj5JLM=',
        'Gaming', 'USER');

INSERT INTO USERS (USERNAME, EMAIL, PASSWORD_HASH, CONTENT_NICHE, ROLE)
VALUES ('techsanjay', 'techsanjay@demo.fullsumppot.com',
        '100000:p6JJ0u6RaP6+GQ/h0cUwCQ==:3Vb/nAEKu+Ly4BSmJwV3gYM92ATmsV3ZuN53wcDqJe0=',
        'Tech', 'USER');

INSERT INTO USERS (USERNAME, EMAIL, PASSWORD_HASH, CONTENT_NICHE, ROLE)
VALUES ('musicriya', 'musicriya@demo.fullsumppot.com',
        '100000:rZAIfKF/pDoM/BhYqGdqUg==:kSFdZj7r7adWiCn5wgiO3Y+wN2y3QNh3PVeQLv5c6/k=',
        'Music', 'USER');

INSERT INTO USERS (USERNAME, EMAIL, PASSWORD_HASH, CONTENT_NICHE, ROLE)
VALUES ('fitnessamit', 'fitnessamit@demo.fullsumppot.com',
        '100000:R4baj94GdAsEMt1tEM/VqA==:P9laK+kilTA1pF9srBEnE2FzqR+70L+9GzcIb3sQXA4=',
        'Fitness', 'USER');

COMMIT;

-- ============================================================
-- COMMUNITIES (created by demo)
-- ============================================================
INSERT INTO COMMUNITIES (NAME, DESCRIPTION, CREATED_BY, CREATED_AT)
VALUES ('Gaming Creators Hub',
        'A community for gaming content creators to grow together',
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '10' DAY);

INSERT INTO COMMUNITIES (NAME, DESCRIPTION, CREATED_BY, CREATED_AT)
VALUES ('Tech & Code Creators',
        'Developers and tech YouTubers supporting each other',
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'),
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '8' DAY);

COMMIT;

-- ============================================================
-- COMMUNITY MEMBERS
-- ============================================================
-- Gaming community: all 4 accounts joined
INSERT INTO COMMUNITY_MEMBERS (COMMUNITY_ID, USER_ID, JOINED_AT, STATUS)
VALUES ((SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Gaming Creators Hub'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '10' DAY, 'ACCEPTED');

INSERT INTO COMMUNITY_MEMBERS (COMMUNITY_ID, USER_ID, JOINED_AT, STATUS)
VALUES ((SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Gaming Creators Hub'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'),
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '7' DAY, 'ACCEPTED');

INSERT INTO COMMUNITY_MEMBERS (COMMUNITY_ID, USER_ID, JOINED_AT, STATUS)
VALUES ((SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Gaming Creators Hub'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'musicriya'),
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '6' DAY, 'ACCEPTED');

INSERT INTO COMMUNITY_MEMBERS (COMMUNITY_ID, USER_ID, JOINED_AT, STATUS)
VALUES ((SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Gaming Creators Hub'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'fitnessamit'),
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '5' DAY, 'ACCEPTED');

-- Tech community
INSERT INTO COMMUNITY_MEMBERS (COMMUNITY_ID, USER_ID, JOINED_AT, STATUS)
VALUES ((SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Tech & Code Creators'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'),
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '8' DAY, 'ACCEPTED');

INSERT INTO COMMUNITY_MEMBERS (COMMUNITY_ID, USER_ID, JOINED_AT, STATUS)
VALUES ((SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Tech & Code Creators'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '4' DAY, 'ACCEPTED');

COMMIT;

-- ============================================================
-- LINKS (demo posts in Gaming community)
-- ============================================================
INSERT INTO LINKS (TITLE, URL, COMMUNITY_ID, USER_ID, CLICKS, CREATED_AT)
VALUES ('My First Gaming Video — Minecraft Survival EP1',
        'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
        (SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Gaming Creators Hub'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        3, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '5' DAY);

INSERT INTO LINKS (TITLE, URL, COMMUNITY_ID, USER_ID, CLICKS, CREATED_AT)
VALUES ('TOP 10 Gaming Setups Under 10000 Rs',
        'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
        (SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Gaming Creators Hub'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        5, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '3' DAY);

INSERT INTO LINKS (TITLE, URL, COMMUNITY_ID, USER_ID, CLICKS, CREATED_AT)
VALUES ('How I Got 1000 Subscribers in 30 Days',
        'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
        (SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Gaming Creators Hub'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        7, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' DAY);

-- techsanjay posts in Tech community
INSERT INTO LINKS (TITLE, URL, COMMUNITY_ID, USER_ID, CLICKS, CREATED_AT)
VALUES ('React vs Next.js — Which One Should You Learn in 2026?',
        'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
        (SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Tech & Code Creators'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'),
        4, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '2' DAY);

INSERT INTO LINKS (TITLE, URL, COMMUNITY_ID, USER_ID, CLICKS, CREATED_AT)
VALUES ('Build a Full Stack App in 1 Hour — ASP.NET + React',
        'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
        (SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Tech & Code Creators'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'),
        6, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' DAY);

-- musicriya posts in Gaming community
INSERT INTO LINKS (TITLE, URL, COMMUNITY_ID, USER_ID, CLICKS, CREATED_AT)
VALUES ('Lofi Music for Gaming Sessions 🎵',
        'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
        (SELECT COMMUNITY_ID FROM COMMUNITIES WHERE NAME = 'Gaming Creators Hub'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'musicriya'),
        2, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '4' DAY);

COMMIT;

-- ============================================================
-- LINK CLICKS (supporters clicking demo's links)
-- ============================================================
-- techsanjay clicked demo's links
INSERT INTO LINK_CLICKS (LINK_ID, CLICKER_USER_ID, CLICK_COUNT, CLICKED_AT)
VALUES ((SELECT LINK_ID FROM LINKS WHERE TITLE = 'My First Gaming Video — Minecraft Survival EP1'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'), 2,
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '4' DAY);

INSERT INTO LINK_CLICKS (LINK_ID, CLICKER_USER_ID, CLICK_COUNT, CLICKED_AT)
VALUES ((SELECT LINK_ID FROM LINKS WHERE TITLE = 'TOP 10 Gaming Setups Under 10000 Rs'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'), 1,
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '2' DAY);

INSERT INTO LINK_CLICKS (LINK_ID, CLICKER_USER_ID, CLICK_COUNT, CLICKED_AT)
VALUES ((SELECT LINK_ID FROM LINKS WHERE TITLE = 'How I Got 1000 Subscribers in 30 Days'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'), 3,
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' DAY);

-- musicriya clicked demo's links
INSERT INTO LINK_CLICKS (LINK_ID, CLICKER_USER_ID, CLICK_COUNT, CLICKED_AT)
VALUES ((SELECT LINK_ID FROM LINKS WHERE TITLE = 'TOP 10 Gaming Setups Under 10000 Rs'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'musicriya'), 1,
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '2' DAY);

INSERT INTO LINK_CLICKS (LINK_ID, CLICKER_USER_ID, CLICK_COUNT, CLICKED_AT)
VALUES ((SELECT LINK_ID FROM LINKS WHERE TITLE = 'How I Got 1000 Subscribers in 30 Days'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'musicriya'), 2,
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' DAY);

-- fitnessamit clicked demo's latest link
INSERT INTO LINK_CLICKS (LINK_ID, CLICKER_USER_ID, CLICK_COUNT, CLICKED_AT)
VALUES ((SELECT LINK_ID FROM LINKS WHERE TITLE = 'How I Got 1000 Subscribers in 30 Days'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'fitnessamit'), 1,
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' DAY);

-- demo clicked techsanjay's links (mutual support)
INSERT INTO LINK_CLICKS (LINK_ID, CLICKER_USER_ID, CLICK_COUNT, CLICKED_AT)
VALUES ((SELECT LINK_ID FROM LINKS WHERE TITLE = 'React vs Next.js — Which One Should You Learn in 2026?'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'), 1,
        SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' DAY);

COMMIT;

-- ============================================================
-- FOLLOWS
-- ============================================================
INSERT INTO FOLLOWS (FOLLOWER_ID, FOLLOWING_ID, STATUS, CREATED_AT)
VALUES ((SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        'ACCEPTED', SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '6' DAY);

INSERT INTO FOLLOWS (FOLLOWER_ID, FOLLOWING_ID, STATUS, CREATED_AT)
VALUES ((SELECT USER_ID FROM USERS WHERE USERNAME = 'musicriya'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        'ACCEPTED', SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '5' DAY);

INSERT INTO FOLLOWS (FOLLOWER_ID, FOLLOWING_ID, STATUS, CREATED_AT)
VALUES ((SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'),
        'ACCEPTED', SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '4' DAY);

INSERT INTO FOLLOWS (FOLLOWER_ID, FOLLOWING_ID, STATUS, CREATED_AT)
VALUES ((SELECT USER_ID FROM USERS WHERE USERNAME = 'fitnessamit'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        'PENDING', SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' DAY);

COMMIT;

-- ============================================================
-- NOTIFICATIONS (for demo account — so the bell looks alive)
-- ============================================================
INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE, IS_READ, CREATED_AT)
VALUES ((SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'),
        'CREATOR_CLICKED_YOUR_LINK',
        '@techsanjay (creator) clicked your link "How I Got 1000 Subscribers in 30 Days"! +1 point 🌟',
        0, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' DAY);

INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE, IS_READ, CREATED_AT)
VALUES ((SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'musicriya'),
        'CREATOR_CLICKED_YOUR_LINK',
        '@musicriya (creator) clicked your link "TOP 10 Gaming Setups Under 10000 Rs"! +1 point 🌟',
        0, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '2' DAY);

INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE, IS_READ, CREATED_AT)
VALUES ((SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'techsanjay'),
        'SUPPORT_BACK',
        '@techsanjay supported your link "My First Gaming Video — Minecraft Survival EP1" back! 🔁',
        0, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '3' DAY);

INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE, IS_READ, CREATED_AT)
VALUES ((SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'fitnessamit'),
        'FOLLOW_REQUEST',
        '@fitnessamit wants to follow you',
        0, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' DAY);

INSERT INTO NOTIFICATIONS (USER_ID, SENDER_ID, TYPE, MESSAGE, IS_READ, CREATED_AT)
VALUES ((SELECT USER_ID FROM USERS WHERE USERNAME = 'demo'),
        (SELECT USER_ID FROM USERS WHERE USERNAME = 'musicriya'),
        'SHOUT_OUT',
        '@musicriya gave you a shout out for supporting their link!',
        1, SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '4' DAY);

COMMIT;

-- ============================================================
-- VERIFY — run these SELECTs to confirm data looks right
-- ============================================================
-- SELECT USERNAME, EMAIL, CONTENT_NICHE FROM USERS WHERE EMAIL LIKE '%fullsumppot.com';
-- SELECT NAME, CREATED_BY FROM COMMUNITIES WHERE CREATED_BY IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%fullsumppot.com');
-- SELECT TITLE, CLICKS FROM LINKS WHERE USER_ID IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%fullsumppot.com');
-- SELECT COUNT(*) FROM LINK_CLICKS WHERE CLICKER_USER_ID IN (SELECT USER_ID FROM USERS WHERE EMAIL LIKE '%fullsumppot.com');
-- SELECT MESSAGE, IS_READ FROM NOTIFICATIONS WHERE USER_ID = (SELECT USER_ID FROM USERS WHERE USERNAME = 'demo');
