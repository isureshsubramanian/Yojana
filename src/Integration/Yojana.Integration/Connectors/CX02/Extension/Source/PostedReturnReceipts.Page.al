page 50010 "Yojana Return Receipts API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'postedReturnReceipt';
    EntitySetName = 'postedReturnReceipts';
    EntityCaption = 'Posted Return Receipt';
    EntitySetCaption = 'Posted Return Receipts';
    SourceTable = "Return Receipt Header";
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
                field(number; Rec."No.") { Caption = 'Number'; Editable = false; }
                field(postingDate; Rec."Posting Date") { Caption = 'Posting Date'; Editable = false; }
                field(customerNumber; Rec."Sell-to Customer No.") { Caption = 'Customer Number'; Editable = false; }
                field(externalDocumentNumber; Rec."External Document No.") { Caption = 'External Document Number'; Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }
}
