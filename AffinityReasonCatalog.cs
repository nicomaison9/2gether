namespace PocketTasks;

public sealed record AffinityTopic(string Id, string Category, string Activity);

public sealed record AffinityReason(string Id, string TopicId, string Category, string Text);

public static class AffinityReasonCatalog
{
    private static readonly string[] Templates =
    [
        "J'aime {activity}.",
        "J'adore {activity}.",
        "Je prends du temps pour {activity}.",
        "Je ne me lasse pas de {activity}.",
        "J'aimerais partager {activity}.",
        "Je cherche quelqu'un pour {activity}.",
        "Mon moment idéal : {activity}.",
        "Je suis toujours partant pour {activity}.",
        "Je préfère {activity} sans me presser.",
        "J'ai souvent envie de {activity}.",
        "Je peux parler longtemps du plaisir de {activity}.",
        "Je garde du temps chaque semaine pour {activity}.",
        "Une sortie parfaite, c'est {activity}.",
        "Le dimanche idéal commence par {activity}."
    ];

    public static IReadOnlyList<AffinityTopic> Topics { get; } =
    [
        new("Randonnée", "Nature & plein air", "faire de la randonnée"),
        new("Balades urbaines", "Nature & plein air", "explorer des quartiers à pied"),
        new("Vélo", "Nature & plein air", "parcourir la ville à vélo"),
        new("Camping", "Nature & plein air", "camper sous les étoiles"),
        new("Escalade", "Sports", "grimper en salle ou en falaise"),
        new("Course à pied", "Sports", "courir au bord du Rhône"),
        new("Yoga", "Sports", "pratiquer le yoga"),
        new("Natation", "Sports", "nager régulièrement"),
        new("Cuisine du monde", "Cuisine", "cuisiner des plats du monde entier"),
        new("Pâtisserie", "Cuisine", "préparer des desserts maison"),
        new("Cuisine végétale", "Cuisine", "cuisiner des recettes végétales"),
        new("Marchés", "Cuisine", "flâner sur les marchés"),
        new("Voyages", "Voyages", "partir découvrir de nouveaux horizons"),
        new("Voyages en train", "Voyages", "voyager en train"),
        new("Week-ends improvisés", "Voyages", "partir en week-end sur un coup de tête"),
        new("Voyage sac à dos", "Voyages", "voyager avec un sac à dos"),
        new("Musique", "Musique", "écouter de la musique"),
        new("Concerts", "Musique", "assister à des concerts"),
        new("Jazz", "Musique", "découvrir des concerts de jazz"),
        new("Vinyles", "Musique", "chiner et écouter des vinyles"),
        new("Cinéma", "Cinéma & séries", "aller au cinéma"),
        new("Documentaires", "Cinéma & séries", "regarder des documentaires"),
        new("Cinéma indépendant", "Cinéma & séries", "découvrir le cinéma indépendant"),
        new("Séries", "Cinéma & séries", "regarder une bonne série"),
        new("Lecture", "Livres & idées", "lire un bon livre"),
        new("Romans", "Livres & idées", "découvrir des romans"),
        new("Sciences", "Livres & idées", "lire sur les sciences"),
        new("Débats", "Livres & idées", "échanger des idées et débattre"),
        new("Arts graphiques", "Arts & création", "découvrir les arts graphiques"),
        new("Photographie", "Arts & création", "faire de la photographie"),
        new("Dessin", "Arts & création", "dessiner"),
        new("Artisanat", "Arts & création", "fabriquer des objets à la main"),
        new("Vie de quartier", "Vie urbaine", "faire vivre les lieux de quartier"),
        new("Cafés de quartier", "Vie urbaine", "découvrir les cafés de quartier"),
        new("Friperies", "Vie urbaine", "chiner dans les friperies"),
        new("Marchés nocturnes", "Vie urbaine", "explorer les marchés nocturnes"),
        new("Animaux", "Animaux & nature", "passer du temps avec des animaux"),
        new("Jardinage urbain", "Animaux & nature", "faire pousser des plantes en ville"),
        new("Oiseaux", "Animaux & nature", "observer les oiseaux"),
        new("Apiculture", "Animaux & nature", "découvrir l'apiculture"),
        new("Jeux de société", "Jeux & culture pop", "jouer à des jeux de société"),
        new("Jeux vidéo", "Jeux & culture pop", "jouer aux jeux vidéo"),
        new("Jeux de rôle", "Jeux & culture pop", "participer à des jeux de rôle"),
        new("Culture pop", "Jeux & culture pop", "suivre la culture pop"),
        new("Engagement associatif", "Engagement", "s'engager dans une association"),
        new("Écologie", "Engagement", "agir pour l'écologie au quotidien"),
        new("Bénévolat", "Engagement", "faire du bénévolat"),
        new("Mobilités douces", "Engagement", "privilégier les mobilités douces"),
        new("Bien-être", "Bien-être", "prendre soin de son équilibre"),
        new("Méditation", "Bien-être", "méditer quelques minutes"),
        new("Pilates", "Bien-être", "pratiquer le Pilates"),
        new("Respiration", "Bien-être", "prendre le temps de respirer"),
        new("Bricolage", "Quotidien & maison", "bricoler chez soi"),
        new("Décoration", "Quotidien & maison", "imaginer une décoration intérieure"),
        new("Brunchs maison", "Quotidien & maison", "préparer un brunch maison"),
        new("Cuisine maison", "Quotidien & maison", "cuisiner des repas maison"),
        new("Théâtre", "Spectacle & scène", "aller voir une pièce de théâtre"),
        new("Improvisation", "Spectacle & scène", "faire de l'improvisation"),
        new("Stand-up", "Spectacle & scène", "découvrir des scènes de stand-up"),
        new("Comédies musicales", "Spectacle & scène", "voir des comédies musicales"),
        new("Technologie", "Tech & sciences", "suivre les nouveautés technologiques"),
        new("Astronomie", "Tech & sciences", "observer le ciel et les étoiles"),
        new("Robotique", "Tech & sciences", "découvrir la robotique"),
        new("Vulgarisation scientifique", "Tech & sciences", "écouter de la vulgarisation scientifique"),
        new("Architecture", "Architecture & patrimoine", "découvrir l'architecture de la ville"),
        new("Patrimoine", "Architecture & patrimoine", "visiter des lieux du patrimoine"),
        new("Histoire urbaine", "Architecture & patrimoine", "apprendre l'histoire des quartiers"),
        new("Monuments", "Architecture & patrimoine", "visiter des monuments"),
        new("Photo argentique", "Photo & image", "photographier en argentique"),
        new("Portraits photo", "Photo & image", "faire des portraits photo"),
        new("Retouche d'image", "Photo & image", "retoucher des images"),
        new("Expositions photo", "Photo & image", "visiter des expositions photo"),
        new("Repas entre amis", "Liens & famille", "réunir des amis autour d'un repas"),
        new("Grandes discussions", "Liens & famille", "avoir de longues conversations"),
        new("Moments en famille", "Liens & famille", "partager des moments en famille"),
        new("Rencontres interculturelles", "Liens & famille", "découvrir d'autres cultures"),
        new("Bars à jeux", "Sorties & soirées", "passer la soirée dans un bar à jeux"),
        new("Bars à vin", "Sorties & soirées", "découvrir des bars à vin"),
        new("Danser en soirée", "Sorties & soirées", "danser en soirée"),
        new("Concerts intimistes", "Sorties & soirées", "assister à des concerts intimistes")
    ];

    public static IReadOnlyList<string> Categories { get; } = new[] { "Toutes les catégories" }
        .Concat(Topics.Select(topic => topic.Category).Distinct())
        .ToArray();

    public static IReadOnlyList<AffinityReason> Reasons { get; } = Topics
        .SelectMany(topic => Templates.Select((template, index) => new AffinityReason(
            $"{topic.Id}:{index}",
            topic.Id,
            topic.Category,
            template.Replace("{activity}", topic.Activity, StringComparison.Ordinal)))
        )
        .ToArray();
}
