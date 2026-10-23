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
                field(sourceType; SourceType) { Caption = 'Source Type'; Editable = false; }
                field(sourceSubtype; SourceSubtype) { Caption = 'Source Subtype'; Editable = false; }
                field(sourceId; SourceId) { Caption = 'Source Id'; Editable = false; }
                field(sourceLineNumber; Rec."Source Ref. No.") { Caption = 'Source Line Number'; Editable = false; }
                field(itemLedgerEntryNumber; Rec."Item Entry No.") { Caption = 'Item Ledger Entry Number'; Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }

    trigger OnAfterGetRecord()
    begin
        SourceType := Format(Rec."Source Type", 0, 9);
        SourceSubtype := Format(Rec."Source Subtype", 0, 9);
        Clear(SourceId);
        if not Evaluate(SourceId, Rec."Source ID") then
            SourceId := Rec.SystemId;
    end;

    var
        SourceId: Guid;
        SourceType: Text;
        SourceSubtype: Text;
}
