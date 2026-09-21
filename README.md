# Le Marché — catalogue de supermarché

Une première version fonctionnelle, responsive et sans dépendance de l’application mobile catalogue. Elle simule une expérience de courses sur téléphone : catalogue par catégories, ajout rapide au panier, quantités, sous-total et total estimé.

## Lancer en 30 secondes

1. Ouvrez un terminal dans ce dossier.
2. Lancez :

   ```bash
   python -m http.server 8000
   ```

3. Ouvrez [http://localhost:8000](http://localhost:8000) dans votre navigateur.

Le serveur local est nécessaire pour que le manifeste et le service worker de l’application installable fonctionnent. Le navigateur peut ensuite proposer **Installer l’application** ou **Ajouter à l’écran d’accueil**.

## Tester

- Choisissez une catégorie dans les boutons en haut.
- Cliquez sur `+` sur plusieurs produits.
- Ouvrez **Panier** en haut (ou dans la barre mobile).
- Modifiez les quantités avec `−` et `+` et vérifiez le total estimé.

Les produits sont définis localement dans `app.js`. Les photos de démonstration proviennent d’Unsplash et nécessitent une connexion internet.
