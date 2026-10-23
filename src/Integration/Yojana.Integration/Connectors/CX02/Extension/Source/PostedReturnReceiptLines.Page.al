page 50011 "Yojana Return Rcpt Lines API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'postedReturnReceiptLine';
    EntitySetName = 'postedReturnReceiptLines';
    EntityCaption = 'Posted Return Receipt Line';
    EntitySetCaption = 'Posted Return Receipt Lines';
    SourceTable = "Return Receipt Line";
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
                field(documentId; DocumentId) { Caption = 'Document Id'; Editable = false; }
                field(lineNumber; Rec."Line No.") { Caption = 'Line Number'; Editable = false; }
                field(itemNumber; Rec."No.") { Caption = 'Item Number'; Editable = false; }
                field(description; Rec.Description) { Caption = 'Description'; Editable = false; }
                field(quantity; Rec.Quantity) { Caption = 'Quantity'; Editable = false; }
                field(unitOfMeasureCode; Rec."Unit of Measure Code") { Caption = 'Unit of Measure Code'; Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }

    trigger OnAfterGetRecord()
    var
        Header: Record "Return Receipt Header";
    begin
        Clear(DocumentId);
        if Header.Get(Rec."Document No.") then
            DocumentId := Header.SystemId;
    end;

    var
        DocumentId: Guid;
}
