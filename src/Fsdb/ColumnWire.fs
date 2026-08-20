/// The one mapping from a *declared* column type to the MySQL wire type id
/// that represents it.
///
/// It lives in its own module, compiled right after `Ast`, because both
/// sides of the build order need it and neither can see the other:
/// `Executor` (which decides a resultset column's type) compiles well before
/// `Protocol` (which writes column definitions and answers COM_FIELD_LIST).
/// Keeping one copy per side is how `Functions` and `Packet` each ended up
/// with their own packet ceiling — see `Limits`.
module Fsdb.ColumnWire

open Fsdb.Ast
open Fsdb.Value

/// The wire type id a column declared as `ty` advertises. Some ids here are
/// the stand-ins from `Value` (`TypeEnumString`, `TypeTinyBool`) rather than
/// real MySQL ids: the distinction they carry lives in the column
/// definition's separate flags/length fields, which only
/// `Protocol.columnDefPayload` writes.
let wireTypeOf (ty: ColumnType) : byte =
    match ty with
    | TTinyInt _ -> TypeTiny
    | TBool -> TypeTinyBool
    | TSmallInt _ -> TypeShort
    | TMediumInt _
    | TInt _ -> TypeLong
    | TBigInt true -> TypeLongLongUnsigned
    | TBigInt false -> TypeLongLong
    | TDecimal _ -> TypeNewDecimal
    | TDouble -> TypeDouble
    | TFloat -> TypeFloat
    | TDate -> TypeDate
    | TDateTime _
    | TTimestamp _ -> TypeDateTime
    | TTime _ -> TypeTime
    | TYear -> TypeYear
    | TEnum _ -> TypeEnumString
    | TBinary _
    | TVarBinary _
    | TTinyBlob
    | TBlob
    | TMediumBlob
    | TLongBlob -> TypeBlob
    // ponytail: MySQL 9's real wire type for VECTOR is 242 — blob + binary
    // charset is what pre-9 clients (MySqlConnector, the 8.4 CLI) see and
    // understand; move to 242 when a client that knows it shows up.
    | TVector _ -> TypeBlob
    | _ -> TypeVarString

/// The subset of `wireTypeOf` a *resultset* column should be forced to when
/// its declared type says more than the stored `Value` does.
///
/// `Executor` types a resultset by reading the row values, which know only
/// what they are, not what they were declared as — so every integer reads
/// back as LONGLONG, every string as VAR_STRING, and a column with no
/// non-NULL value in any row has nothing to read at all.
///
/// `None` means "keep the data-driven answer". That is deliberate for the
/// string family: fsdb stores CHAR/TEXT/JSON alike as `VString`, and their
/// declared ids differ from what MySQL reports for the *expression* in ways
/// this mapping alone can't settle, so overriding them would trade one
/// mismatch for another. The types listed here are the ones whose declared
/// id is what the data-driven read already produces when a row exists, so
/// forcing it only changes the empty-column case — plus the four whose
/// stored `Value` genuinely loses the distinction (TIME, ENUM, BOOLEAN,
/// YEAR).
let resultTypeOf (ty: ColumnType) : byte option =
    match ty with
    | TTime _
    | TEnum _
    | TBool
    | TYear
    | TTinyInt false
    | TSmallInt false
    | TMediumInt false
    | TInt false
    | TBigInt _
    | TDecimal _
    | TDouble
    | TFloat
    | TDate
    | TDateTime _
    | TTimestamp _ -> Some(wireTypeOf ty)
    | _ -> None
