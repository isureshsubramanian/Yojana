page 50005 "Yojana Service Items API"
{
    PageType = API;
    APIPublisher = 'yojana';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'serviceItem';
    EntitySetName = 'serviceItems';
    EntityCaption = 'Service Item';
    EntitySetCaption = 'Service Items';
    SourceTable = "Service Item";
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
                field(serialNumber; Rec."Serial No.") { Caption = 'Serial Number'; Editable = false; }
                field(itemNumber; Rec."Item No.") { Caption = 'Item Number'; Editable = false; }
                field(customerNumber; Rec."Customer No.") { Caption = 'Customer Number'; Editable = false; }
                field(status; Rec.Status) { Caption = 'Status'; Editable = false; }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { Caption = 'Last Modified Date Time'; Editable = false; }
            }
        }
    }
}
