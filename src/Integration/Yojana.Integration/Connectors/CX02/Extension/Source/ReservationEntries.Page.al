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
    SourceTable = "Reservation Entry";
    ODataKeyFields = SystemId;
    DelayedInsert = true;
    Extensible = false;
    InsertAllowed = false;
    ModifyAllowed = false;
    DeleteAllowed = false;
    ChangeTrackingAllowed = true;

    layout
    {
        area(Content)
        {
            repeater(Entries)
            {
                field(id; Rec.SystemId) { Caption = 'Id'; Editable = false; }
                field(itemNumber; Rec."Item No.") { Caption = 'Item Number'; Editable = false; }
                field(locationCode; Rec."Location Code") { Caption = 'Location Code'; Editable = false; }
                field(quantity; Rec.Quantity) { Caption = 'Quantity'; Editable = false; }
                field(quantityBase; Rec."Quantity (Base)") { Caption = 'Quantity Base'; Editable = false; }
                field(reservationStatus; Rec."Reservation Status") { Caption = 'Reservation Status'; Editable = false; }
                field(sourceDocumentType; SourceDocumentType) { Caption = 'Source Document Type'; Editable = false; }
                field(sourceDocumentNumber; Rec."Source ID") { Caption = 'Source Document Number'; Editable = false; }
                field(sourceLineNumber; Rec."Source Ref. No.") { Caption = 'Source Line Number'; Editable = false; }
                field(sourceDocumentId; SourceDocumentId) { Caption = 'Source Document Id'; Editable = false; }
                field(serialNumber; Rec."Serial No.") { Caption = 'Serial Number'; Editable = false; }
                field(lotNumber; Rec."Lot No.") { Caption = 'Lot Number'; Editable = false; }
                field(packageNumber; Rec."Package No.") { Caption = 'Package Number'; Editable = false; }
                field(positive; Rec.Positive) { Caption = 'Positive'; Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }

    trigger OnAfterGetRecord()
    begin
        References.Resolve(Format(Rec."Source Type", 0, 9), Rec."Source ID",
            SourceDocumentType, SourceDocumentId);
    end;

    var
        References: Codeunit "Yojana Source Document Ref";
        SourceDocumentId: Guid;
        SourceDocumentType: Text;
}
