# Project Hero - Low Poly Tactical RPG Prototype

## Project Structure
This folder is structured as a Unity Project.

- **Assets/**: Contains all game assets and scripts.
  - **Scripts/**: C# Source code.
    - **Core/**: Core systems (Physics, Timeline, Pathfinding).
    - **Demos/**: Demo scripts to test systems.
  - **Art/**: Placeholders for Low Poly models and materials.
  - **Scenes/**: Unity scenes.
  - **Prefabs/**: Unit and UI prefabs.

## How to Open
1. Open **Unity Hub**.
2. Click **Add** -> **Add project from disk**.
3. Select this `ProjectHero` folder.
4. Open the project (Recommended Unity Version: 2022.3 LTS or later).

## Key Systems
- **Physics Engine**: Deterministic momentum-based combat ($P=mv$).
- **Battle Timeline**: Priority-queue based event system with "Interaction Windows".
- **Pathfinding**: A* implementation for triangular/hexagonal grids.

## Design Document
See `Game_Design_Final.md` in this folder for the full design specification.
