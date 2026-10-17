SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.AppUsers', N'U') IS NULL
    CREATE TABLE dbo.AppUsers (
        Username nvarchar(50) COLLATE Latin1_General_100_CI_AS NOT NULL PRIMARY KEY,
        PasswordHash nvarchar(500) NOT NULL,
        Role varchar(20) NOT NULL CHECK (Role IN ('Admin', 'Seller')),
        FailedAttempts int NOT NULL DEFAULT 0,
        LockedUntil datetimeoffset NULL
    );
IF OBJECT_ID(N'dbo.Products', N'U') IS NULL
    CREATE TABLE dbo.Products (
        Id nvarchar(100) NOT NULL PRIMARY KEY,
        Stock int NOT NULL CHECK (Stock >= 0),
        Data nvarchar(max) NOT NULL CHECK (ISJSON(Data) = 1),
        CreatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET()
    );
IF OBJECT_ID(N'dbo.Sales', N'U') IS NULL
    CREATE TABLE dbo.Sales (
        Id bigint IDENTITY NOT NULL PRIMARY KEY,
        RequestId uniqueidentifier NOT NULL UNIQUE,
        Data nvarchar(max) NOT NULL CHECK (ISJSON(Data) = 1)
    );
IF OBJECT_ID(N'dbo.CustomerRequests', N'U') IS NULL
    CREATE TABLE dbo.CustomerRequests (
        Id bigint IDENTITY NOT NULL PRIMARY KEY,
        Data nvarchar(max) NOT NULL CHECK (ISJSON(Data) = 1)
    );
COMMIT;
