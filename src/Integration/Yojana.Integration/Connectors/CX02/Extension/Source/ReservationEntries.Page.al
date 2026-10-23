page 50003 "Yojana Reservations API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'reservationEntry';
    EntitySetName = 'reservationEntries';
    EntityCaption = 'Reservation Entry';
    EntitySetCaption = 'Reservation Entries';
    SourceTable = "Yojana Tracking Request";
    ODataKeyFields = SystemId;
    DelayedInsert = true;
    Extensible = false;
    InsertAllowed = true;
    ModifyAllowed = false;
    DeleteAllowed = false;
    ChangeTrackingAllowed = true;

    layout
    {
        area(Content)
        {
            repeater(Entries)
            {
                field(id; Rec.SystemId) { Caption = 'Id'; }
                field(itemNumber; Rec."Item Number") { Caption = 'Item Number'; }
                field(locationCode; Rec."Location Code") { Caption = 'Location Code'; }
                field(quantity; Rec.Quantity) { Caption = 'Quantity'; }
                field(quantityBase; Rec."Quantity Base") { Caption = 'Quantity Base'; }
                field(reservationStatus; Rec."Reservation Status") { Caption = 'Reservation Status'; }
                field(sourceType; Rec."Source Type") { Caption = 'Source Type'; }
                field(sourceSubtype; Rec."Source Subtype") { Caption = 'Source Subtype'; }
                field(sourceId; Rec."Source Id") { Caption = 'Source Id'; }
                field(sourceLineNumber; Rec."Source Line Number") { Caption = 'Source Line Number'; }
                field(serialNumber; Rec."Serial Number") { Caption = 'Serial Number'; }
                field(lotNumber; Rec."Lot Number") { Caption = 'Lot Number'; }
                field(packageNumber; Rec."Package Number") { Caption = 'Package Number'; }
                field(positive; Rec.Positive) { Caption = 'Positive'; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }

    trigger OnInsertRecord(BelowxRec: Boolean): Boolean
    begin
        if IsNullGuid(Rec.SystemId) then
            Rec.SystemId := CreateGuid();
        if Rec."Quantity Base" = 0 then
            Rec."Quantity Base" := Rec.Quantity;
        if Rec."Reservation Status" = '' then
            Rec."Reservation Status" := 'Surplus';
        Rec.Insert(true, true);
        exit(false);
    end;

    [ServiceEnabled]
    [Scope('Cloud')]
    procedure postTrackedDocument(documentType: Text; documentId: Guid; logicalReference: Text; metadata: JsonObject; tracking: JsonArray; var actionContext: WebServiceActionContext)
    var
        Posting: Codeunit "Yojana Tracked Document Post";
    begin
        Posting.Post(Rec, documentType, documentId, logicalReference, metadata, tracking, actionContext);
    end;
}
