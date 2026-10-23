page 50008 "Yojana Transfer Receipts API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'postedTransferReceipt';
    EntitySetName = 'postedTransferReceipts';
    EntityCaption = 'Posted Transfer Receipt';
    EntitySetCaption = 'Posted Transfer Receipts';
    SourceTable = "Transfer Receipt Header";
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
                field(transferFromCode; Rec."Transfer-from Code") { Caption = 'Transfer From Code'; Editable = false; }
                field(transferToCode; Rec."Transfer-to Code") { Caption = 'Transfer To Code'; Editable = false; }
                field(externalDocumentNumber; Rec."External Document No.") { Caption = 'External Document Number'; Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }
}
