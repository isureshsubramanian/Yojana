page 50012 "Yojana Post Result API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'postTrackedDocumentResponse';
    EntitySetName = 'postTrackedDocumentResponses';
    EntityCaption = 'Post Tracked Document Response';
    EntitySetCaption = 'Post Tracked Document Responses';
    SourceTable = "Yojana Tracking Request";
    ODataKeyFields = SystemId;
    DelayedInsert = true;
    Extensible = false;
    InsertAllowed = false;
    ModifyAllowed = false;
    DeleteAllowed = false;

    layout
    {
        area(Content)
        {
            repeater(Entries)
            {
                field(id; Rec."Posted Document Id") { Caption = 'Id'; Editable = false; }
                field(documentNumber; Rec."Posted Document Number") { Caption = 'Document Number'; Editable = false; }
                field(status; Status) { Caption = 'Status'; Editable = false; }
                field(postedAt; Rec."Posted At") { Caption = 'Posted At'; Editable = false; }
            }
        }
    }

    trigger OnAfterGetRecord()
    begin
        Status := 'posted';
    end;

    var
        Status: Text;
}
