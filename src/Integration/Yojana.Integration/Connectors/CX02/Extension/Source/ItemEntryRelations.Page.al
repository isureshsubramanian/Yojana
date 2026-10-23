page 50002 "Yojana Item Relations API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'itemEntryRelation';
    EntitySetName = 'itemEntryRelations';
    EntityCaption = 'Item Entry Relation';
    EntitySetCaption = 'Item Entry Relations';
    SourceTable = "Item Entry Relation";
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
                field(sourceDocumentType; SourceDocumentType) { Caption = 'Source Document Type'; Editable = false; }
                field(sourceDocumentNumber; Rec."Source ID") { Caption = 'Source Document Number'; Editable = false; }
                field(sourceLineNumber; Rec."Source Ref. No.") { Caption = 'Source Line Number'; Editable = false; }
                field(sourceDocumentId; SourceDocumentId) { Caption = 'Source Document Id'; Editable = false; }
                field(itemLedgerEntryNumber; Rec."Item Entry No.") { Caption = 'Item Ledger Entry Number'; Editable = false; }
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
