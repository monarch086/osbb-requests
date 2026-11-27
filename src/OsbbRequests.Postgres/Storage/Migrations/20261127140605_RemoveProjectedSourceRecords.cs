using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OsbbRequests.DataImport.Storage;

namespace OsbbRequests.Postgres.Storage.Migrations;

[DbContext(typeof(DataContext))]
[Migration("20261127140605_RemoveProjectedSourceRecords")]
public sealed class RemoveProjectedSourceRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");
        migrationBuilder.Sql("""
            CREATE FUNCTION pg_temp.same_import_time(stored timestamptz, imported text)
            RETURNS boolean LANGUAGE sql AS $$
                SELECT CASE WHEN stored IS NULL OR imported IS NULL THEN stored IS NULL AND imported IS NULL
                    ELSE abs(extract(epoch FROM stored - imported::timestamptz)) <= 0.000001 END;
            $$;

            WITH records AS (
                SELECT "SourceId" AS sid, "Table" AS tab, "Key" AS key, "Data" AS d,
                       split_part("Key", chr(31), 1) AS pk, split_part("Key", chr(31), 2) AS sk,
                       CASE WHEN pg_input_is_valid("Data" ->> 'payload', 'jsonb') THEN ("Data" ->> 'payload')::jsonb END AS p,
                       "Data" -> 'recordData' AS record
                FROM source_records
            ),
            duplicates AS (
                SELECT s.sid, s.tab, s.key FROM records s
                WHERE (s.tab = 'command-data' AND s.pk LIKE 'USER#%'
                       AND s.sk IN ('REQUEST_DRAFT', 'REQUEST_LOOKUP', 'PROFILE_DIALOG'))
                   OR (s.tab = 'residents' AND s.d ->> 'sourcePk' LIKE 'USER#%' AND s.d ->> 'sourceSk' = 'PROFILE_DIALOG')
                UNION ALL
                SELECT s.sid, s.tab, s.key FROM records s
                JOIN complexes c ON c."SourceId" = s.sid AND c."ExternalId" = s.p ->> 'BuildingId'
                JOIN residents r ON r."ComplexId" = c."Id" AND r."TelegramUserId"::text = s.p ->> 'UserId'
                LEFT JOIN resident_phones phone ON phone."ResidentId" = r."Id" AND phone."IsPrimary"
                LEFT JOIN resident_consents consent ON consent."ResidentId" = r."Id"
                WHERE s.tab = 'residents' AND split_part(s.pk, '#', 3) = 'USER'
                  AND s.d - ARRAY['pk','payload','version','buildingNumber','complexId','dateOrder','directory','nameOrder','residentType'] = '{}'::jsonb
                  AND s.p - ARRAY['Id','UserId','BuildingId','Stage','Version','CreatedAt','UpdatedAt','FirstName','LastName',
                      'DecisionReason','BuildingNumber','ApartmentNumber','PersonalDataDeleted','PersonalDataDeletedAt','ApprovedAt',
                      'Email','Phone','ConsentAt','ConsentVersion','ResumeAfterConsent','ResidentType','TypeBeforeAddress','TypeChangeCount'] = '{}'::jsonb
                  AND (s.d ->> 'version')::integer = r."Version"
                  AND ROW(r."RegistrationId",r."Stage",r."Version",r."FirstName",r."LastName",r."DecisionReason",
                      r."BuildingNumber",r."ApartmentNumber",r."PersonalDataDeleted",r."Email",r."ResumeAfterConsent",
                      r."ResidentType",r."TypeBeforeAddress",r."TypeChangeCount",phone."Phone",consent."Version")
                      IS NOT DISTINCT FROM ROW(s.p ->> 'Id',s.p ->> 'Stage',(s.p ->> 'Version')::integer,
                      s.p ->> 'FirstName',s.p ->> 'LastName',s.p ->> 'DecisionReason',s.p ->> 'BuildingNumber',s.p ->> 'ApartmentNumber',
                      coalesce((s.p ->> 'PersonalDataDeleted')::boolean,false),s.p ->> 'Email',s.p ->> 'ResumeAfterConsent',
                      coalesce(s.p ->> 'ResidentType','FamilyMember'),coalesce((s.p ->> 'TypeBeforeAddress')::boolean,false),
                      coalesce((s.p ->> 'TypeChangeCount')::integer,0),
                      CASE WHEN NOT (r."PersonalDataDeleted" OR r."Stage" = 'Deleted') THEN nullif(s.p ->> 'Phone','') END,
                      CASE WHEN NOT (r."PersonalDataDeleted" OR r."Stage" = 'Deleted') THEN s.p ->> 'ConsentVersion' END)
                  AND pg_temp.same_import_time(r."CreatedAt",s.p ->> 'CreatedAt')
                  AND pg_temp.same_import_time(r."UpdatedAt",s.p ->> 'UpdatedAt')
                  AND pg_temp.same_import_time(r."ApprovedAt",s.p ->> 'ApprovedAt')
                  AND pg_temp.same_import_time(r."PersonalDataDeletedAt",s.p ->> 'PersonalDataDeletedAt')
                  AND pg_temp.same_import_time(consent."ConsentedAt",
                      CASE WHEN NOT (r."PersonalDataDeleted" OR r."Stage" = 'Deleted') THEN s.p ->> 'ConsentAt' END)
                UNION ALL
                SELECT s.sid, s.tab, s.key FROM records s
                JOIN complexes c ON c."SourceId" = s.sid AND c."ExternalId" = s.p ->> 'BuildingId'
                JOIN requests r ON r."SourceId" = s.sid AND r."ComplexId" = c."Id"
                    AND r."OwnerId"::text = s.p ->> 'OwnerId' AND r."RequestId"::text = s.p ->> 'Id'
                WHERE s.tab = 'command-data' AND s.pk LIKE 'USER#%' AND s.sk LIKE 'REQUEST#%'
                  AND s.d - ARRAY['pk','sk','payload','version','requestBuildingId','requestCategory','requestCreatedAt','requestFloor',
                      'requestPriorityCreatedAt','requestStatus','requestTarget','searchText'] = '{}'::jsonb
                  AND s.p - ARRAY['Id','OwnerId','BuildingId','Category','Location','Description','CreatedAt','Status','Visibility',
                      'Resolution','AssignedTo','Priority','PlannedDate','Scope','BuildingNumber','Floor','Entrance','KeyType','KeyQuantity','KeyItems'] = '{}'::jsonb
                  AND ROW(r."Version",r."Status",r."Visibility",r."Category",r."Description",r."Location",r."AssignedTo",
                      r."Priority",r."PlannedDate",r."Resolution",r."Scope",r."BuildingNumber",r."Floor",r."Entrance",r."KeyType",r."KeyQuantity")
                      IS NOT DISTINCT FROM ROW((s.d ->> 'version')::integer,
                      (ARRAY['New','InProgress','AwaitingClarification','Completed','Rejected'])[(s.p ->> 'Status')::integer+1],
                      (ARRAY['Private','Public'])[(s.p ->> 'Visibility')::integer+1],s.p ->> 'Category',s.p ->> 'Description',
                      s.p ->> 'Location',s.p ->> 'AssignedTo',coalesce(s.p ->> 'Priority','Normal'),(s.p ->> 'PlannedDate')::date,
                      s.p ->> 'Resolution',s.p ->> 'Scope',s.p ->> 'BuildingNumber',(s.p ->> 'Floor')::integer,
                      (s.p ->> 'Entrance')::integer,s.p ->> 'KeyType',(s.p ->> 'KeyQuantity')::integer)
                  AND pg_temp.same_import_time(r."CreatedAt",s.p ->> 'CreatedAt')
                  AND coalesce(nullif(s.p -> 'KeyItems','null'::jsonb),'[]'::jsonb) = coalesce((
                      SELECT jsonb_agg(jsonb_build_object('Type',k."Type",'Phone',k."Phone",'SerialNumber',k."SerialNumber") ORDER BY k."Position")
                      FROM request_key_items k WHERE k."SourceId" = r."SourceId" AND k."OwnerId" = r."OwnerId" AND k."RequestId" = r."RequestId"
                  ), '[]'::jsonb)
                UNION ALL
                SELECT s.sid, s.tab, s.key FROM records s
                JOIN request_events e ON e."SourceId" = s.sid AND e."Key" = s.key
                LEFT JOIN request_event_snapshots v ON v."SourceId" = e."SourceId" AND v."Key" = e."Key"
                WHERE s.tab = 'command-data' AND s.pk LIKE 'REQUEST#%' AND s.sk LIKE 'EVENT#%'
                  AND s.d - ARRAY['pk','sk','action','actor','actorId','createdAt','status','previousStatus',
                      'resolution','target','floor','entrance','changes'] = '{}'::jsonb
                  AND pg_temp.same_import_time(e."CreatedAt",s.d ->> 'createdAt')
                  AND ROW(e."Action",e."Actor",e."ActorId",e."Status",e."PreviousStatus",e."Resolution",e."HasResolution",
                      e."Target",e."Floor",e."HasFloor",e."Entrance",e."HasEntrance") IS NOT DISTINCT FROM
                      ROW(s.d ->> 'action',s.d ->> 'actor',(s.d ->> 'actorId')::bigint,s.d ->> 'status',s.d ->> 'previousStatus',
                      s.d ->> 'resolution',s.d ? 'resolution',s.d ->> 'target',(s.d ->> 'floor')::integer,s.d ? 'floor',
                      (s.d ->> 'entrance')::integer,s.d ? 'entrance')
                  AND (s.d ->> 'changes')::jsonb IS NOT DISTINCT FROM CASE WHEN v."Key" IS NOT NULL THEN
                      jsonb_build_object('AssignedTo',v."AssignedTo",'Priority',v."Priority",'PlannedDate',v."PlannedDate",
                          'Visibility',v."Visibility",'Scope',v."Scope",'BuildingNumber',v."BuildingNumber",'Floor',v."Floor")
                      || CASE WHEN v."HasEntrance" THEN jsonb_build_object('Entrance',v."Entrance") ELSE '{}'::jsonb END
                      || CASE WHEN v."KeySerialNumbersChanged" IS NOT NULL THEN
                          jsonb_build_object('KeySerialNumbersChanged',v."KeySerialNumbersChanged") ELSE '{}'::jsonb END END
                UNION ALL
                SELECT s.sid, s.tab, s.key FROM records s
                JOIN resident_events e ON e."SourceId" = s.sid AND e."Key" = s.key
                CROSS JOIN LATERAL (SELECT (s.record ->> 'payload')::jsonb AS p) v
                WHERE s.tab = 'residents' AND s.d ->> 'sourcePk' LIKE 'REGISTRATION#%' AND s.d ->> 'sourceSk' LIKE 'EVENT#%'
                  AND s.d - ARRAY['complexId','pk','recordData','recordGroup','recordIndexGroup','recordOrder','sourcePk','sourceSk'] = '{}'::jsonb
                  AND s.record - ARRAY['pk','sk','actor','createdAt','decision','reason','approvalMode','payload'] = '{}'::jsonb
                  AND (v.p IS NULL OR v.p - ARRAY['Actor','CreatedAt','PreviousType','NewType','FirstNameChanged','LastNameChanged','KeyChange'] = '{}'::jsonb)
                  AND (nullif(v.p -> 'KeyChange','null'::jsonb) IS NULL OR
                       (v.p -> 'KeyChange') - ARRAY['RequestId','Previous','Current'] = '{}'::jsonb
                       AND ((v.p -> 'KeyChange') -> 'Previous') - ARRAY['Type','SerialNumber'] = '{}'::jsonb
                       AND (nullif((v.p -> 'KeyChange') -> 'Current','null'::jsonb) IS NULL OR
                            ((v.p -> 'KeyChange') -> 'Current') - ARRAY['Type','SerialNumber'] = '{}'::jsonb))
                  AND pg_temp.same_import_time(e."CreatedAt",coalesce(v.p ->> 'CreatedAt',s.record ->> 'createdAt'))
                  AND ROW(e."Actor",e."Decision",e."Reason",e."ApprovalMode",e."PreviousType",e."NewType",e."FirstNameChanged",
                      e."LastNameChanged",e."KeyRequestId",e."PreviousKeyType",e."PreviousKeySerialNumber",e."CurrentKeyType",e."CurrentKeySerialNumber")
                      IS NOT DISTINCT FROM ROW(coalesce(v.p ->> 'Actor',s.record ->> 'actor'),s.record ->> 'decision',
                      s.record ->> 'reason',s.record ->> 'approvalMode',v.p ->> 'PreviousType',v.p ->> 'NewType',
                      (v.p ->> 'FirstNameChanged')::boolean,(v.p ->> 'LastNameChanged')::boolean,(v.p #>> '{KeyChange,RequestId}')::bigint,
                      v.p #>> '{KeyChange,Previous,Type}',v.p #>> '{KeyChange,Previous,SerialNumber}',
                      v.p #>> '{KeyChange,Current,Type}',v.p #>> '{KeyChange,Current,SerialNumber}')
                UNION ALL
                SELECT s.sid, s.tab, s.key FROM records s
                JOIN resident_memberships m ON m."SourceId" = s.sid AND m."Key" = s.key
                JOIN complexes c ON c."SourceId" = m."SourceId" AND c."Id" = m."ComplexId"
                WHERE s.tab = 'residents' AND s.d ->> 'sourceSk' = 'MEMBERSHIP'
                  AND s.d - ARRAY['complexId','pk','recordData','recordGroup','recordOrder','sourcePk','sourceSk'] = '{}'::jsonb
                  AND s.record - ARRAY['pk','sk','active','buildingId','registrationId','reviewedBy','reviewedAt'] = '{}'::jsonb
                  AND c."ExternalId" = s.record ->> 'buildingId'
                  AND s.d ->> 'sourcePk' = 'USER#' || m."TelegramUserId"
                  AND ROW(m."Active",m."RegistrationId",m."ReviewedBy") IS NOT DISTINCT FROM
                      ROW((s.record ->> 'active')::boolean,s.record ->> 'registrationId',s.record ->> 'reviewedBy')
                  AND pg_temp.same_import_time(m."ReviewedAt",s.record ->> 'reviewedAt')
                UNION ALL
                SELECT s.sid, s.tab, s.key FROM records s
                JOIN administrator_events e ON e."SourceId" = s.sid AND e."Key" = s.key
                WHERE s.tab = 'command-data' AND s.pk LIKE 'ADMIN#%' AND s.sk LIKE 'EVENT#%'
                  AND s.d - ARRAY['pk','sk','action','actor','createdAt','permissions'] = '{}'::jsonb
                  AND pg_temp.same_import_time(e."CreatedAt",s.d ->> 'createdAt')
                  AND ROW(e."Action",e."Actor",CASE WHEN e."ComplexRequests" IS NOT NULL THEN jsonb_build_object(
                      'ComplexRequests',e."ComplexRequests",'KeyOrders',e."KeyOrders",'RegisteredResidents',e."RegisteredResidents",
                      'PendingResidents',e."PendingResidents",'Messages',e."Messages") END) IS NOT DISTINCT FROM
                      ROW(s.d ->> 'action',s.d ->> 'actor',(s.d ->> 'permissions')::jsonb)
                UNION ALL
                SELECT s.sid, s.tab, s.key FROM records s
                JOIN administrators a ON a."SourceId" = s.sid AND a."UserId" = s.key
                JOIN administrator_permissions q ON q."SourceId" = a."SourceId" AND q."UserId" = a."UserId"
                JOIN administrator_notification_settings n ON n."SourceId" = a."SourceId" AND n."UserId" = a."UserId"
                WHERE s.tab = 'administrators'
                  AND s.d - ARRAY['UserId','Email','DisplayName','Version','IsActive','IsSuperAdmin','Directory','CreatedAt','UpdatedAt',
                      'AccessComplexRequests','AccessKeyOrders','AccessRegisteredResidents','AccessPendingResidents','AccessMessages',
                      'TelegramChatId','NotifyNewResidents','NotifyNewRequests','NotifyNewKeyOrders','NotifyNewAdministrators','NotifyNewApartmentRegistrations'] = '{}'::jsonb
                  AND pg_temp.same_import_time(a."CreatedAt",s.d ->> 'CreatedAt')
                  AND pg_temp.same_import_time(a."UpdatedAt",s.d ->> 'UpdatedAt')
                  AND ROW(a."Email",a."DisplayName",a."Version",a."IsActive",a."IsSuperAdmin",q."ComplexRequests",q."KeyOrders",
                      q."RegisteredResidents",q."PendingResidents",q."Messages",n."TelegramChatId",n."NewResidents",n."NewRequests",
                      n."NewKeyOrders",n."NewAdministrators",n."NewApartmentRegistrations")
                      IS NOT DISTINCT FROM ROW(s.d ->> 'Email',s.d ->> 'DisplayName',coalesce((s.d ->> 'Version')::integer,0),
                      coalesce((s.d ->> 'IsActive')::boolean,false),coalesce((s.d ->> 'IsSuperAdmin')::boolean,false),
                      coalesce((s.d ->> 'AccessComplexRequests')::boolean,true),coalesce((s.d ->> 'AccessKeyOrders')::boolean,true),
                      coalesce((s.d ->> 'AccessRegisteredResidents')::boolean,true),coalesce((s.d ->> 'AccessPendingResidents')::boolean,true),
                      coalesce((s.d ->> 'AccessMessages')::boolean,false),s.d ->> 'TelegramChatId',
                      coalesce((s.d ->> 'NotifyNewResidents')::boolean,false),coalesce((s.d ->> 'NotifyNewRequests')::boolean,false),
                      coalesce((s.d ->> 'NotifyNewKeyOrders')::boolean,(s.d ->> 'NotifyNewRequests')::boolean,false),
                      coalesce((s.d ->> 'NotifyNewAdministrators')::boolean,false),
                      coalesce((s.d ->> 'NotifyNewApartmentRegistrations')::boolean,(s.d ->> 'NotifyNewResidents')::boolean,false))
                UNION ALL
                SELECT s.sid, s.tab, s.key FROM records s
                JOIN api_daily_statistics v ON v."SourceId" = s.sid AND s.sk = v."Date"::text || '#' || v."Endpoint"
                WHERE s.tab = 'statistics' AND s.pk = 'API'
                  AND s.d - ARRAY['pk','sk','requests','readUnits','writeUnits','durationMs'] = '{}'::jsonb
                  AND ROW(v."Requests",v."ReadUnits",v."WriteUnits",v."DurationMs") IS NOT DISTINCT FROM ROW(
                      coalesce((s.d ->> 'requests')::bigint,0),coalesce((s.d ->> 'readUnits')::numeric,0),
                      coalesce((s.d ->> 'writeUnits')::numeric,0),coalesce((s.d ->> 'durationMs')::numeric,0))
                UNION ALL
                SELECT s.sid, s.tab, s.key FROM records s
                JOIN broadcast_messages b ON b."SourceId" = s.sid AND b."MessageId" = s.p ->> 'Id'
                JOIN complexes c ON c."Id" = b."ComplexId" AND c."SourceId" = b."SourceId" AND s.pk = 'MESSAGES#' || c."ExternalId"
                WHERE s.tab = 'command-data' AND (s.sk LIKE 'MESSAGE#%' AND b."Key" = s.key OR s.sk = 'ID#' || b."MessageId")
                  AND s.d - ARRAY['pk','sk','payload'] = '{}'::jsonb
                  AND s.p - ARRAY['Id','Text','CreatedAt','CreatedById','CreatedBy'] = '{}'::jsonb
                  AND pg_temp.same_import_time(b."CreatedAt",s.p ->> 'CreatedAt')
                  AND ROW(b."Text",b."CreatedById",b."CreatedBy") IS NOT DISTINCT FROM ROW(s.p ->> 'Text',s.p ->> 'CreatedById',s.p ->> 'CreatedBy')
            )
            DELETE FROM source_records AS source USING duplicates AS d
            WHERE source."SourceId" = d.sid AND source."Table" = d.tab AND source."Key" = d.key;

            DROP FUNCTION pg_temp.same_import_time(timestamptz, text);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Retired source copies are not recreated by a schema rollback.
    }
}
