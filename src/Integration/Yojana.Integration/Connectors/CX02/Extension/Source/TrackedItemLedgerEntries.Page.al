page 50001 "Yojana Tracked Ledger API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'trackedItemLedgerEntry';
    EntitySetName = 'trackedItemLedgerEntries';
    EntityCaption = 'Tracked Item Ledger Entry';
    EntitySetCaption = 'Tracked Item Ledger Entries';
    SourceTable = "Item Ledger Entry";
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
                field(entryNumber; Rec."Entry No.") { Caption = 'Entry Number'; Editable = false; }
                field(itemNumber; Rec."Item No.") { Caption = 'Item Number'; Editable = false; }
                field(postingDate; Rec."Posting Date") { Caption = 'Posting Date'; Editable = false; }
                field(entryType; Rec."Entry Type") { Caption = 'Entry Type'; Editable = false; }
                field(sourceNumber; Rec."Source No.") { Caption = 'Source Number'; Editable = false; }
                field(sourceType; Rec."Source Type") { Caption = 'Source Type'; Editable = false; }
                field(documentNumber; Rec."Document No.") { Caption = 'Document Number'; Editable = false; }
                field(documentType; Rec."Document Type") { Caption = 'Document Type'; Editable = false; }
                field(description; Rec.Description) { Caption = 'Description'; Editable = false; }
                field(locationCode; Rec."Location Code") { Caption = 'Location Code'; Editable = false; }
                field(quantity; Rec.Quantity) { Caption = 'Quantity'; Editable = false; }
                field(serialNumber; Rec."Serial No.") { Caption = 'Serial Number'; Editable = false; }
                field(lotNumber; Rec."Lot No.") { Caption = 'Lot Number'; Editable = false; }
                field(packageNumber; Rec."Package No.") { Caption = 'Package Number'; Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }
}
