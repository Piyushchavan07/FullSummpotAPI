-- =============================================================================
-- Phone Verification Migration
-- Run this once against your Oracle DB (FULLSUMPOT schema)
-- Adds PHONE_NUMBER + IS_PHONE_VERIFIED to USERS, and creates PHONE_OTPS table
-- =============================================================================

-- 1. Add PHONE_NUMBER column (nullable, unique so no two users share a number)
ALTER TABLE USERS ADD PHONE_NUMBER VARCHAR2(20) NULL;
ALTER TABLE USERS ADD CONSTRAINT UQ_USERS_PHONE UNIQUE (PHONE_NUMBER);

-- 2. Add IS_PHONE_VERIFIED flag (mirrors IS_VERIFIED for email)
ALTER TABLE USERS ADD IS_PHONE_VERIFIED NUMBER(1) DEFAULT 0 NOT NULL;

-- 3. Phone OTP storage table (mirrors EMAIL_OTPS structure)
CREATE TABLE PHONE_OTPS (
    OTP_ID       NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    PHONE_NUMBER VARCHAR2(20)  NOT NULL,
    OTP_CODE     VARCHAR2(6)   NOT NULL,
    PURPOSE      VARCHAR2(20)  NOT NULL,  -- 'VERIFY_PHONE' or 'RESET_PASSWORD'
    EXPIRES_AT   TIMESTAMP     NOT NULL,
    USED         NUMBER(1)     DEFAULT 0  NOT NULL,
    CREATED_AT   TIMESTAMP     DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP)
);

-- Index: speed up the lookup that runs on every OTP validation
CREATE INDEX IDX_PHONE_OTPS_LOOKUP
    ON PHONE_OTPS (PHONE_NUMBER, PURPOSE, USED, EXPIRES_AT);

COMMIT;
