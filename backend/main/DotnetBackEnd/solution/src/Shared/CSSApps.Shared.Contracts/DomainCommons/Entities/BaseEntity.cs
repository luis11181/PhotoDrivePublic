using static CSSApps.Shared.Contracts.Constants.CommonConstants;

namespace CSSApps.Shared.Contracts.DomainCommons.Entities;

public abstract class BaseEntity
{
    public string? Status { get; set; } = EntityStatusType.Active;
}

public abstract class AuditableEntity : BaseEntity
{
    // // these annotation does not guarantee that the database will generate the value, this must be set in the db context model builder
    // [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTime? CreatedDate { get; set; }

    public string? CreatedBy { get; set; }

    // // these annotation does not guarantee that the database will generate the value, this must be set in the db context model builder
    // [DatabaseGenerated(DatabaseGeneratedOption.Computed)] //The database generates a value when a row is inserted or updated.
    public DateTime? UpdatedDate { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime? DeletedDate { get; set; }

    public string? DeletedBy { get; set; }
}

// code to override equality of an entity

// int? _requestedHashCode;
// int _Id;
// public virtual int Id
// {
//   get
//   {
//     return _Id;
//   }
//   protected set
//   {
//     _Id = value;
//   }
// }

// public bool IsTransient()
// {
//   return this.Id == default;
// }

// public override bool Equals(object obj)
// {
//   if (obj == null || !(obj is Entity))
//     return false;

//   if (Object.ReferenceEquals(this, obj))
//     return true;

//   if (this.GetType() != obj.GetType())
//     return false;

//   Entity item = (Entity)obj;

//   if (item.IsTransient() || this.IsTransient())
//     return false;
//   else
//     return item.Id == this.Id;
// }

// public override int GetHashCode()
// {
//   if (!IsTransient())
//   {
//     if (!_requestedHashCode.HasValue)
//       _requestedHashCode = this.Id.GetHashCode() ^ 31; // XOR for random distribution (http://blogs.msdn.com/b/ericlippert/archive/2011/02/28/guidelines-and-rules-for-gethashcode.aspx)

//     return _requestedHashCode.Value;
//   }
//   else
//     return base.GetHashCode();

// }
// public static bool operator ==(Entity left, Entity right)
// {
//   if (Object.Equals(left, null))
//     return (Object.Equals(right, null)) ? true : false;
//   else
//     return left.Equals(right);
// }

// public static bool operator !=(Entity left, Entity right)
// {
//   return !(left == right);
// }
