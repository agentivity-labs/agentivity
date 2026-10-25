namespace Agentivity.Domain.Arguments;

public enum ArgumentType
{
    Text, // string libre
    Number, // double dans le state
    Boolean, // bool dans le state
    Credentials, // référence à un credential (ID)
    ImageRef, // instance de ImageRef (single image)
    ImagesRef, // instance de WorkflowImagesRef (collection of images)
    AudioRef, // instance de AudioRef (single audio file)
    TextList, // liste de strings
    Choice, // sélection parmi des valeurs prédéfinies
    Structured, // document JSON structuré

    /// <summary>
    /// Référence, par ID, à un asset du workspace (Data Table, …) — le genre d'asset est porté par
    /// <see cref="ArgumentUiMetadata.AssetKind"/>. Comme <see cref="Credentials"/> : la valeur
    /// stockée est l'ID, le Studio affiche le nom et propose un sélecteur.
    /// </summary>
    AssetRef,
}
