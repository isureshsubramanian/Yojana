table 50000 "Yojana Tracking Request"
{
    Caption = 'Yojana Tracking Request';
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer)
        {
            AutoIncrement = true;
            Caption = 'Entry No.';
            DataClassification = SystemMetadata;
        }
        field(2; "Item Number"; Code[20])
        {
            Caption = 'Item Number';
            DataClassification = CustomerContent;
        }
        field(3; "Location Code"; Code[10])
        {
            Caption = 'Location Code';
            DataClassification = CustomerContent;
        }
        field(4; Quantity; Decimal)
        {
            Caption = 'Quantity';
            DataClassification = CustomerContent;
        }
        field(5; "Quantity Base"; Decimal)
        {
            Caption = 'Quantity Base';
            DataClassification = CustomerContent;
        }
        field(6; "Reservation Status"; Text[30])
        {
            Caption = 'Reservation Status';
            DataClassification = CustomerContent;
        }
        field(7; "Source Type"; Text[30])
        {
            Caption = 'Source Type';
            DataClassification = CustomerContent;
        }
        field(8; "Source Subtype"; Text[30])
        {
            Caption = 'Source Subtype';
            DataClassification = CustomerContent;
        }
        field(9; "Source Id"; Guid)
        {
            Caption = 'Source Id';
            DataClassification = SystemMetadata;
        }
        field(10; "Source Line Number"; Integer)
        {
            Caption = 'Source Line Number';
            DataClassification = SystemMetadata;
        }
        field(11; "Serial Number"; Code[50])
        {
            Caption = 'Serial Number';
            DataClassification = CustomerContent;
        }
        field(12; "Lot Number"; Code[50])
        {
            Caption = 'Lot Number';
            DataClassification = CustomerContent;
        }
        field(13; "Package Number"; Code[50])
        {
            Caption = 'Package Number';
            DataClassification = CustomerContent;
        }
        field(14; Positive; Boolean)
        {
            Caption = 'Positive';
            DataClassification = SystemMetadata;
        }
        field(20; "Logical Reference"; Text[100])
        {
            Caption = 'Logical Reference';
            DataClassification = CustomerContent;
        }
        field(21; "Posted Document Id"; Guid)
        {
            Caption = 'Posted Document Id';
            DataClassification = SystemMetadata;
        }
        field(22; "Posted Document Number"; Code[20])
        {
            Caption = 'Posted Document Number';
            DataClassification = CustomerContent;
        }
        field(23; "Posted At"; DateTime)
        {
            Caption = 'Posted At';
            DataClassification = SystemMetadata;
        }
    }

    keys
    {
        key(PK; "Entry No.")
        {
            Clustered = true;
        }
        key(Source; "Source Id", "Source Line Number")
        {
        }
        key(LogicalReference; "Logical Reference")
        {
        }
    }
}
