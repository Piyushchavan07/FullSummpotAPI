-- ============================================================
-- FullSumppot — DELETE ALL DATA (keeps tables, drops rows only)
-- Run in Oracle SQL Developer
-- Order matters: child tables deleted before parent tables
-- Admin users (ROLE = 'ADMIN') are preserved
-- ============================================================

-- Messages & Conversations (all)
DELETE FROM MESSAGES;
DELETE FROM CONVERSATION_PARTICIPANTS;
DELETE FROM CONVERSATIONS;

-- Link activity — only for non-admin users
DELETE FROM LINK_COMMENTS  WHERE USER_ID   IN (SELECT USER_ID FROM USERS WHERE ROLE != 'ADMIN');
DELETE FROM LINK_LIKES     WHERE USER_ID   IN (SELECT USER_ID FROM USERS WHERE ROLE != 'ADMIN');
DELETE FROM LINK_CLICKS    WHERE CLICKER_USER_ID IN (SELECT USER_ID FROM USERS WHERE ROLE != 'ADMIN');
DELETE FROM LINKS          WHERE USER_ID   IN (SELECT USER_ID FROM USERS WHERE ROLE != 'ADMIN');

-- Community activity — only for non-admin users
DELETE FROM COMMUNITY_MEMBERS WHERE USER_ID IN (SELECT USER_ID FROM USERS WHERE ROLE != 'ADMIN');
DELETE FROM COMMUNITIES    WHERE CREATED_BY IN (SELECT USER_ID FROM USERS WHERE ROLE != 'ADMIN');

-- Social — only for non-admin users
DELETE FROM NOTIFICATIONS  WHERE USER_ID   IN (SELECT USER_ID FROM USERS WHERE ROLE != 'ADMIN');
DELETE FROM FOLLOWS        WHERE FOLLOWER_ID  IN (SELECT USER_ID FROM USERS WHERE ROLE != 'ADMIN')
                              OR FOLLOWING_ID  IN (SELECT USER_ID FROM USERS WHERE ROLE != 'ADMIN');

-- Users — keep admins
DELETE FROM USERS WHERE ROLE != 'ADMIN';

COMMIT;

-- Verify — admins should remain, everything else 0
SELECT 'USERS (non-admin)'          AS TBL, COUNT(*) AS CNT FROM USERS                   WHERE ROLE != 'ADMIN' UNION ALL
SELECT 'USERS (admin kept)',                COUNT(*)         FROM USERS                   WHERE ROLE  = 'ADMIN' UNION ALL
SELECT 'COMMUNITIES',                       COUNT(*)         FROM COMMUNITIES              UNION ALL
SELECT 'COMMUNITY_MEMBERS',                 COUNT(*)         FROM COMMUNITY_MEMBERS        UNION ALL
SELECT 'LINKS',                             COUNT(*)         FROM LINKS                    UNION ALL
SELECT 'LINK_CLICKS',                       COUNT(*)         FROM LINK_CLICKS              UNION ALL
SELECT 'LINK_LIKES',                        COUNT(*)         FROM LINK_LIKES               UNION ALL
SELECT 'LINK_COMMENTS',                     COUNT(*)         FROM LINK_COMMENTS            UNION ALL
SELECT 'FOLLOWS',                           COUNT(*)         FROM FOLLOWS                  UNION ALL
SELECT 'NOTIFICATIONS',                     COUNT(*)         FROM NOTIFICATIONS            UNION ALL
SELECT 'CONVERSATIONS',                     COUNT(*)         FROM CONVERSATIONS            UNION ALL
SELECT 'CONVERSATION_PARTICIPANTS',         COUNT(*)         FROM CONVERSATION_PARTICIPANTS UNION ALL
SELECT 'MESSAGES',                          COUNT(*)         FROM MESSAGES
ORDER BY TBL;
-- USERS (non-admin) = 0, USERS (admin kept) = your admin count, all others = 0
