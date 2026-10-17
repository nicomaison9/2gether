pipeline {
    agent {
        label 'dotnet-10-linux-playwright'
    }

    options {
        skipDefaultCheckout(true)
        disableConcurrentBuilds()
        buildDiscarder(logRotator(numToKeepStr: '20'))
    }

    environment {
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = 'true'
        NUGET_XMLDOC_MODE = 'skip'
    }

    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }

        stage('Vérifier le SDK .NET 10') {
            steps {
                sh "dotnet --list-sdks | grep -E '^[[:space:]]*10\\.'"
            }
        }

        stage('Restaurer les outils') {
            steps {
                sh 'dotnet tool restore'
            }
        }

        stage('Restaurer et auditer les dépendances') {
            steps {
                sh 'dotnet restore Server/Tests/TwoGether.SecurityTests.csproj -p:NuGetAudit=true -p:NuGetAuditLevel=low -p:TreatWarningsAsErrors=true'
            }
        }

        stage('Détecter les dépendances vulnérables') {
            steps {
                sh 'dotnet list Server/Tests/TwoGether.SecurityTests.csproj package --vulnerable --include-transitive'
            }
        }

        stage('Compiler') {
            steps {
                sh 'dotnet build Server/Tests/TwoGether.SecurityTests.csproj --configuration Release --no-restore'
            }
        }

        stage('Installer Chromium') {
            steps {
                sh 'pwsh Server/Tests/bin/Release/net10.0/playwright.ps1 install chromium'
            }
        }

        stage('Tests de non-régression') {
            steps {
                sh 'dotnet test Server/Tests/TwoGether.SecurityTests.csproj --configuration Release --no-build'
            }
        }

        stage('Vérifier les migrations EF Core') {
            steps {
                sh 'dotnet tool run dotnet-ef migrations has-pending-model-changes --project Server/TwoGether.Server.csproj --startup-project Server/TwoGether.Server.csproj --configuration Release'
            }
        }

        stage('Publier un artefact de validation') {
            steps {
                sh 'dotnet publish Server/TwoGether.Server.csproj --configuration Release --no-build --output Server/bin/Release/net10.0/jenkins-publish'
                archiveArtifacts artifacts: 'Server/bin/Release/net10.0/jenkins-publish/**', fingerprint: true
            }
        }
    }

    post {
        success {
            echo 'Build, tests de non-régression, migrations et publication de validation réussis.'
        }
        failure {
            echo 'Échec de la pipeline : consulter l’étape en erreur avant de fusionner.'
        }
    }
}
